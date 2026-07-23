using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cdp.ScriptableIde;

/// <summary>
/// Node engine policy — same shape as <see cref="TfmPolicy"/>.
/// LTS SSOT: Node dist index <c>lts</c> field (string codename or false), not prose.
/// </summary>
public enum EnginePolicy
{
    PreferMostUsed = 0,
    Latest = 1,
    Lts = 2,
    Specified = 3
}

/// <summary>Resolve Node major for new typescript projects from vendor meta.</summary>
public static class EngineResolver
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly object CacheLock = new();
    private static DateTimeOffset _cacheAt;
    private static IReadOnlyList<NodeRelease>? _cache;

    public static async Task<(string EngineRange, string Detail)> ResolveAsync(
        EnginePolicy policy,
        string? specified,
        string? scanRoot,
        CancellationToken ct = default)
    {
        if (policy == EnginePolicy.Specified)
        {
            if (string.IsNullOrWhiteSpace(specified))
                throw new ArgumentException("EnginePolicy.Specified requires engines= (e.g. >=20).");
            return (specified.Trim(), "specified");
        }

        var releases = await LoadNodeReleasesAsync(ct).ConfigureAwait(false);
        var installed = await ListInstalledNodeMajorsAsync(ct).ConfigureAwait(false);

        return policy switch
        {
            EnginePolicy.Latest => ResolveLatest(releases, installed),
            EnginePolicy.Lts => ResolveLts(releases, installed),
            EnginePolicy.PreferMostUsed => ResolveMostUsed(scanRoot, releases, installed),
            _ => ResolveLatest(releases, installed)
        };
    }

    public static EnginePolicy ParsePolicy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return EnginePolicy.PreferMostUsed;
        return raw.Trim().ToLowerInvariant() switch
        {
            "prefer_most_used" or "most_used" or "prefermostused" => EnginePolicy.PreferMostUsed,
            "latest" => EnginePolicy.Latest,
            "lts" => EnginePolicy.Lts,
            "specified" or "exact" => EnginePolicy.Specified,
            _ => throw new ArgumentException(
                $"Unknown engine_policy '{raw}'. Use prefer_most_used|latest|lts|specified.")
        };
    }

    private static (string, string) ResolveLts(
        IReadOnlyList<NodeRelease> releases,
        IReadOnlyList<int> installed)
    {
        // Official flag: lts is string (codename) when LTS line, false otherwise.
        var ltsMajors = releases
            .Where(r => r.IsLts)
            .Select(r => r.Major)
            .Distinct()
            .OrderByDescending(m => m)
            .ToList();
        if (ltsMajors.Count == 0)
            return (">=20", "node_lts_meta_empty_fallback_20");

        var amongInstalled = ltsMajors.Where(installed.Contains).ToList();
        var pick = amongInstalled.Count > 0 ? amongInstalled.Max() : ltsMajors[0];
        var detail = amongInstalled.Count > 0
            ? $"node_lts_field meta major={pick}"
            : $"node_lts_field catalog_latest={pick} (not_installed)";
        return ($">={pick}", detail);
    }

    private static (string, string) ResolveLatest(
        IReadOnlyList<NodeRelease> releases,
        IReadOnlyList<int> installed)
    {
        if (installed.Count > 0)
            return ($">={installed.Max()}", $"node_latest_installed={installed.Max()}");
        var current = releases.Select(r => r.Major).DefaultIfEmpty(20).Max();
        return ($">={current}", $"node_latest_catalog={current}");
    }

    private static (string, string) ResolveMostUsed(
        string? scanRoot,
        IReadOnlyList<NodeRelease> releases,
        IReadOnlyList<int> installed)
    {
        // Scan package.json engines.node if present — else LTS
        if (!string.IsNullOrWhiteSpace(scanRoot) && Directory.Exists(scanRoot))
        {
            var counts = new Dictionary<int, int>();
            foreach (var pkg in Directory.EnumerateFiles(scanRoot, "package.json", SearchOption.AllDirectories)
                         .Take(40))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(pkg));
                    if (!doc.RootElement.TryGetProperty("engines", out var eng) ||
                        !eng.TryGetProperty("node", out var node))
                        continue;
                    var raw = node.GetString();
                    if (raw is null)
                        continue;
                    var m = System.Text.RegularExpressions.Regex.Match(raw, @"(\d+)");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var maj))
                        counts[maj] = counts.GetValueOrDefault(maj) + 1;
                }
                catch
                {
                    // skip
                }
            }

            if (counts.Count > 0)
            {
                var best = counts.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key).First();
                return ($">={best.Key}", $"node_most_used={best.Key} count={best.Value}");
            }
        }

        return ResolveLts(releases, installed);
    }

    private static async Task<IReadOnlyList<NodeRelease>> LoadNodeReleasesAsync(CancellationToken ct)
    {
        lock (CacheLock)
        {
            if (_cache is not null && DateTimeOffset.UtcNow - _cacheAt < VendorCatalog.Current.CacheTtl)
                return _cache;
        }

        try
        {
            var url = VendorCatalog.Current.NodeDistIndexUrl;
            var rows = await Http.GetFromJsonAsync<List<NodeDistRow>>(url, ct).ConfigureAwait(false);
            var list = (rows ?? [])
                .Select(NodeRelease.TryParse)
                .Where(r => r is not null)
                .Select(r => r!)
                .ToList();
            lock (CacheLock)
            {
                _cache = list;
                _cacheAt = DateTimeOffset.UtcNow;
            }

            return list;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<List<int>> ListInstalledNodeMajorsAsync(CancellationToken ct)
    {
        try
        {
            var (code, stdout, _) = await ProcessUtil.RunAsync("node", ["-v"], null, null, ct)
                .ConfigureAwait(false);
            if (code != 0)
                return [];
            var m = System.Text.RegularExpressions.Regex.Match(stdout, @"v?(?<maj>\d+)");
            return m.Success ? [int.Parse(m.Groups["maj"].Value)] : [];
        }
        catch
        {
            return [];
        }
    }

    private sealed class NodeDistRow
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        /// <summary>false or LTS codename string — official machine flag.</summary>
        [JsonPropertyName("lts")]
        public JsonElement Lts { get; set; }
    }

    private sealed record NodeRelease(int Major, bool IsLts)
    {
        public static NodeRelease? TryParse(NodeDistRow row)
        {
            if (string.IsNullOrWhiteSpace(row.Version))
                return null;
            var v = row.Version.TrimStart('v');
            if (!int.TryParse(v.Split('.')[0], out var major) || major <= 0)
                return null;
            var isLts = row.Lts.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(row.Lts.GetString());
            return new NodeRelease(major, isLts);
        }
    }
}
