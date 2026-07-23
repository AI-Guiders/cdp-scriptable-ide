using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Cdp.ScriptableIde;

/// <summary>How harness picks TargetFramework for new csharp projects.</summary>
public enum TfmPolicy
{
    /// <summary>Mode of TFMs in nearby projects; fallback <see cref="Latest"/>.</summary>
    PreferMostUsed = 0,
    /// <summary>Highest major from installed SDKs → net{N}.0.</summary>
    Latest = 1,
    /// <summary>
    /// Highest installed major whose channel has <c>release-type=lts</c> in the
    /// configured releases-index (<see cref="VendorCatalog"/>), not parity heuristic.
    /// </summary>
    Lts = 2,
    /// <summary>Exact TFM from caller (<c>tfm=</c>) — rare escape.</summary>
    Specified = 3
}

public static partial class TfmResolver
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly object CacheLock = new();
    private static DateTimeOffset _cacheAt;
    private static IReadOnlyList<ReleaseChannel>? _cacheChannels;

    [GeneratedRegex(@"^\s*(?<ver>\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SdkLine();

    [GeneratedRegex(@"net(?<maj>\d+)(?:\.(?<min>\d+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TfmToken();

    public static async Task<(string Tfm, string Detail)> ResolveAsync(
        TfmPolicy policy,
        string? specifiedTfm,
        string? scanRoot,
        CancellationToken ct = default)
    {
        if (policy == TfmPolicy.Specified)
        {
            if (string.IsNullOrWhiteSpace(specifiedTfm))
                throw new ArgumentException("TfmPolicy.Specified requires tfm= (e.g. net10.0).");
            var norm = NormalizeTfm(specifiedTfm!);
            return (norm, "specified");
        }

        var sdks = await ListSdkMajorsAsync(ct).ConfigureAwait(false);
        if (sdks.Count == 0)
            return ("net10.0", "no_sdks_fallback_net10");

        return policy switch
        {
            TfmPolicy.Latest => (ToTfm(sdks.Max()), $"latest_sdk_major={sdks.Max()}"),
            TfmPolicy.Lts => await ResolveLtsAsync(sdks, ct).ConfigureAwait(false),
            TfmPolicy.PreferMostUsed => ResolveMostUsed(scanRoot, sdks),
            _ => (ToTfm(sdks.Max()), "default_latest")
        };
    }

    public static TfmPolicy ParsePolicy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return TfmPolicy.PreferMostUsed;
        return raw.Trim().ToLowerInvariant() switch
        {
            "prefer_most_used" or "most_used" or "prefermostused" => TfmPolicy.PreferMostUsed,
            "latest" => TfmPolicy.Latest,
            "lts" => TfmPolicy.Lts,
            "specified" or "exact" => TfmPolicy.Specified,
            _ => throw new ArgumentException(
                $"Unknown tfm_policy '{raw}'. Use prefer_most_used|latest|lts|specified.")
        };
    }

    private static async Task<(string Tfm, string Detail)> ResolveLtsAsync(
        IReadOnlyList<int> sdkMajors,
        CancellationToken ct)
    {
        var channels = await LoadReleaseChannelsAsync(ct).ConfigureAwait(false);
        if (channels.Count == 0)
            return FallbackEvenMajorLts(sdkMajors, "lts_index_unavailable_parity_fallback");

        // SSOT: release-type == lts; skip preview/eol. Prefer active then maintenance.
        var ltsInstalled = channels
            .Where(c => c.ReleaseType.Equals("lts", StringComparison.OrdinalIgnoreCase))
            .Where(c => c.SupportPhase is "active" or "maintenance")
            .Select(c => c.Major)
            .Where(m => m > 0 && sdkMajors.Contains(m))
            .Distinct()
            .ToList();

        if (ltsInstalled.Count > 0)
        {
            var best = ltsInstalled.Max();
            var phase = channels.First(c => c.Major == best &&
                c.ReleaseType.Equals("lts", StringComparison.OrdinalIgnoreCase)).SupportPhase;
            return (ToTfm(best), $"lts_release_type meta major={best} phase={phase}");
        }

        // Catalog has LTS but none installed — fall back to latest installed with honest detail
        var catalogLts = channels
            .Where(c => c.ReleaseType.Equals("lts", StringComparison.OrdinalIgnoreCase))
            .Where(c => c.SupportPhase is "active" or "maintenance")
            .Select(c => c.Major)
            .DefaultIfEmpty()
            .Max();
        return (ToTfm(sdkMajors.Max()),
            catalogLts > 0
                ? $"lts_no_installed_match catalog_lts={catalogLts} fallback_latest={sdkMajors.Max()}"
                : $"lts_empty_catalog fallback_latest={sdkMajors.Max()}");
    }

    /// <summary>Offline / fetch failure only — even-major cadence is not SSOT.</summary>
    private static (string Tfm, string Detail) FallbackEvenMajorLts(
        IReadOnlyList<int> sdkMajors,
        string reason)
    {
        var lts = sdkMajors.Where(m => m % 2 == 0).DefaultIfEmpty().Max();
        if (lts <= 0)
            return (ToTfm(sdkMajors.Max()), $"{reason}_then_latest={sdkMajors.Max()}");
        return (ToTfm(lts), $"{reason} major={lts}");
    }

    private static async Task<IReadOnlyList<ReleaseChannel>> LoadReleaseChannelsAsync(CancellationToken ct)
    {
        lock (CacheLock)
        {
            if (_cacheChannels is not null && DateTimeOffset.UtcNow - _cacheAt < VendorCatalog.Current.CacheTtl)
                return _cacheChannels;
        }

        foreach (var url in VendorCatalog.Current.DotnetReleasesIndexUrls)
        {
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    continue;
                var doc = await resp.Content.ReadFromJsonAsync<ReleasesIndexDoc>(cancellationToken: ct)
                    .ConfigureAwait(false);
                var list = (doc?.ReleasesIndex ?? [])
                    .Select(ReleaseChannel.TryParse)
                    .Where(c => c is not null)
                    .Select(c => c!)
                    .ToList();
                if (list.Count == 0)
                    continue;
                lock (CacheLock)
                {
                    _cacheChannels = list;
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
                // try next mirror
            }
        }

        return [];
    }

    private static (string Tfm, string Detail) ResolveMostUsed(string? scanRoot, IReadOnlyList<int> sdkMajors)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(scanRoot) && Directory.Exists(scanRoot))
        {
            foreach (var csproj in Directory.EnumerateFiles(scanRoot, "*.csproj", SearchOption.AllDirectories)
                         .Take(80))
            {
                try
                {
                    foreach (var tfm in ReadTfms(csproj))
                        counts[tfm] = counts.GetValueOrDefault(tfm) + 1;
                }
                catch
                {
                    // skip unreadable
                }
            }
        }

        if (counts.Count > 0)
        {
            var best = counts.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key).First();
            var maj = MajorOf(best.Key);
            if (maj is int m && sdkMajors.Contains(m))
                return (NormalizeTfm(best.Key), $"most_used={best.Key} count={best.Value}");
            return (NormalizeTfm(best.Key), $"most_used={best.Key} count={best.Value} (sdk_not_listed)");
        }

        var latest = ToTfm(sdkMajors.Max());
        return (latest, $"most_used_empty_fallback_latest={latest}");
    }

    private static IEnumerable<string> ReadTfms(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);
        foreach (var el in doc.Descendants().Where(e =>
                     e.Name.LocalName is "TargetFramework" or "TargetFrameworks"))
        {
            var text = el.Value?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;
            foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return NormalizeTfm(part);
        }
    }

    private static async Task<List<int>> ListSdkMajorsAsync(CancellationToken ct)
    {
        var (code, stdout, _) = await ProcessUtil.RunAsync("dotnet", ["--list-sdks"], null, null, ct)
            .ConfigureAwait(false);
        var majors = new HashSet<int>();
        if (code != 0)
            return [];
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = SdkLine().Match(line);
            if (!m.Success)
                continue;
            var ver = m.Groups["ver"].Value;
            var major = int.Parse(ver.Split('.')[0]);
            majors.Add(major);
        }

        return majors.OrderBy(x => x).ToList();
    }

    private static string ToTfm(int major) => $"net{major}.0";

    private static string NormalizeTfm(string raw)
    {
        var t = raw.Trim();
        if (t.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            return t.ToLowerInvariant();
        if (int.TryParse(t, out var maj))
            return ToTfm(maj);
        return t.ToLowerInvariant();
    }

    private static int? MajorOf(string tfm)
    {
        var m = TfmToken().Match(tfm);
        return m.Success ? int.Parse(m.Groups["maj"].Value) : null;
    }

    private sealed class ReleasesIndexDoc
    {
        [JsonPropertyName("releases-index")]
        public List<ReleaseIndexRow>? ReleasesIndex { get; set; }
    }

    private sealed class ReleaseIndexRow
    {
        [JsonPropertyName("channel-version")]
        public string? ChannelVersion { get; set; }

        [JsonPropertyName("release-type")]
        public string? ReleaseType { get; set; }

        [JsonPropertyName("support-phase")]
        public string? SupportPhase { get; set; }
    }

    private sealed record ReleaseChannel(int Major, string ReleaseType, string SupportPhase)
    {
        public static ReleaseChannel? TryParse(ReleaseIndexRow row)
        {
            if (string.IsNullOrWhiteSpace(row.ChannelVersion) || string.IsNullOrWhiteSpace(row.ReleaseType))
                return null;
            var majorPart = row.ChannelVersion.Split('.', 2)[0];
            if (!int.TryParse(majorPart, out var major) || major <= 0)
                return null;
            return new ReleaseChannel(
                major,
                row.ReleaseType.Trim().ToLowerInvariant(),
                (row.SupportPhase ?? "").Trim().ToLowerInvariant());
        }
    }
}
