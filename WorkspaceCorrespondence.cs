#nullable enable
using System.Text.Json;
using System.Text.RegularExpressions;
using Tomlyn;

namespace Cdp.ScriptableIde;

/// <summary>
/// Standalone L1 correspondence from <c>.cascade/workspace.toml</c> (ADR 0061 / 0155 / 0156).
/// Forward: path → feature + ADR docs. Reverse: explicit <c>code_anchors</c>.
/// </summary>
public static partial class WorkspaceCorrespondence
{
    public const string Schema = "correspondence/v0";

    public sealed record ForwardDoc(string Path, string Title);

    public sealed record ReverseAnchor(
        string DocPath,
        string DocTitle,
        string Provenance,
        string Kind,
        string File,
        int? LineStart,
        int? LineEnd,
        string? MemberKey,
        string Wire);

    public sealed record Result(
        string WorkspaceRoot,
        string? FileRel,
        string? FeatureLine,
        string[] FeatureDocs,
        string AdrLine,
        ForwardDoc[] ForwardDocs,
        ReverseAnchor[] ReverseAnchors,
        string[] ActiveLayers,
        string TomlPath);

    public static string? FindWorkspaceRoot(string? startPath, string? hintRoot = null)
    {
        foreach (var candidate in CandidateRoots(startPath, hintRoot))
        {
            var toml = Path.Combine(candidate, ".cascade", "workspace.toml");
            if (File.Exists(toml))
                return candidate;
        }

        return null;
    }

    public static Result? TryResolve(string absoluteFilePath, string? workspaceRootHint = null)
    {
        if (string.IsNullOrWhiteSpace(absoluteFilePath))
            return null;

        string abs;
        try { abs = Path.GetFullPath(absoluteFilePath.Trim()); }
        catch { return null; }

        var root = FindWorkspaceRoot(abs, workspaceRootHint);
        if (root is null)
            return null;

        var tomlPath = Path.Combine(root, ".cascade", "workspace.toml");
        WorkspaceTomlDoc? doc;
        try
        {
            doc = TomlSerializer.Deserialize<WorkspaceTomlDoc>(
                File.ReadAllText(tomlPath),
                new TomlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        }
        catch
        {
            return null;
        }

        var rel = TryRel(root, abs);
        if (rel is null)
            return null;

        var feature = ResolveFeature(doc, rel);
        var featureLine = BuildFeatureLine(feature);
        var featureDocs = feature?.Docs?
            .Select(NormalizeDoc)
            .Where(static d => d.Length > 0)
            .ToArray() ?? [];

        var docs = new List<string>(featureDocs);
        foreach (var m in ResolveAdrMap(doc, rel))
        {
            if (!docs.Contains(m, StringComparer.OrdinalIgnoreCase))
                docs.Add(m);
        }

        var auto = NormalizeAutoInclude(doc?.Workspace?.Adr?.AutoInclude);
        var maxRelated = doc?.Workspace?.Adr?.MaxRelated is int mr && mr > 0 ? mr : 8;
        if (auto == "linked" && docs.Count > 0)
        {
            var primary = docs[0];
            var absPrimary = Path.Combine(root, primary.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(absPrimary))
            {
                var linked = ExtractLinkedAdrs(
                    File.ReadAllText(absPrimary),
                    primary,
                    NormalizeAdrRoot(doc?.Workspace?.Adr?.RootDir));
                var baseCount = docs.Count;
                foreach (var l in linked)
                {
                    if (docs.Count >= baseCount + maxRelated)
                        break;
                    if (docs.Contains(l, StringComparer.OrdinalIgnoreCase))
                        continue;
                    docs.Add(l);
                }
            }
        }

        var forward = docs
            .Select(p => new ForwardDoc(p, GuessTitle(p)))
            .ToArray();

        var reverse = LoadReverse(doc, root, docs, rel);
        var layers = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(featureLine)) layers.Add("L1p");
        if (forward.Length > 0) layers.Add("L1");
        if (reverse.Length > 0) layers.Add("L1r");

        return new Result(
            root,
            rel.Replace('\\', '/'),
            string.IsNullOrWhiteSpace(featureLine) ? null : featureLine,
            featureDocs,
            BuildAdrLine(docs),
            forward,
            reverse,
            layers.ToArray(),
            tomlPath);
    }

    public static IdeReport ToIdeReport(CodeAnchor anchor, Result? result)
    {
        if (result is null)
        {
            return new IdeReport
            {
                Kind = "correspondence",
                Available = false,
                Reason = "no_workspace_toml",
                Anchor = IdeReportAnchor.From(anchor),
                Summary = "No .cascade/workspace.toml above this file — open a CIDE-marked repo or pass workspace root.",
                Highlights = [],
                Next = ["cdp_open scm root with .cascade/workspace.toml", "analysis_scene feature=correspondence path="]
            };
        }

        var highlights = new List<IdeReportHighlight>();
        foreach (var d in result.ForwardDocs.Take(12))
            highlights.Add(new IdeReportHighlight { Path = d.Path, Why = $"forward · {d.Title}" });
        foreach (var r in result.ReverseAnchors.Take(8))
            highlights.Add(new IdeReportHighlight { Path = r.Wire, Why = $"reverse · {r.DocTitle} ({r.Provenance})" });

        var summary = string.IsNullOrWhiteSpace(result.FeatureLine)
            ? (result.AdrLine.Length > 0 ? result.AdrLine : "No ADR/feature map for this path")
            : result.FeatureLine + (result.AdrLine.Length > 0 ? " · " + result.AdrLine : "");

        return new IdeReport
        {
            Kind = "correspondence",
            Available = true,
            Anchor = IdeReportAnchor.From(anchor),
            Summary = summary,
            Highlights = highlights,
            Next =
            [
                "analysis_scene feature=correspondence path=",
                "Open forward doc anchor [F:docs/adr/…]",
                "Reverse wire → sniper/edit"
            ]
        };
    }

    /// <summary>Replace stub: resolve from file path (walk-up) or optional root hint.</summary>
    public static IdeReport ResolveReport(CodeAnchor anchor, string? workspaceRootHint = null)
    {
        var file = anchor.FilePath;
        if (string.IsNullOrWhiteSpace(file))
        {
            return new IdeReport
            {
                Kind = "correspondence",
                Available = false,
                Reason = "no_file",
                Anchor = IdeReportAnchor.From(anchor),
                Summary = "Correspondence needs CodeAnchor with file path.",
                Highlights = [],
                Next = ["Pass file_path / open buffer"]
            };
        }

        var result = TryResolve(file, workspaceRootHint);
        return ToIdeReport(anchor, result);
    }

    static IEnumerable<string> CandidateRoots(string? startPath, string? hintRoot)
    {
        if (!string.IsNullOrWhiteSpace(hintRoot))
        {
            string h;
            try { h = Path.GetFullPath(hintRoot.Trim()); }
            catch { h = hintRoot.Trim(); }
            yield return h;
        }

        if (string.IsNullOrWhiteSpace(startPath))
            yield break;

        string cur;
        try
        {
            cur = File.Exists(startPath)
                ? Path.GetDirectoryName(Path.GetFullPath(startPath)) ?? ""
                : Path.GetFullPath(startPath);
        }
        catch
        {
            yield break;
        }

        while (!string.IsNullOrWhiteSpace(cur))
        {
            yield return cur;
            var parent = Path.GetDirectoryName(cur);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, cur, StringComparison.OrdinalIgnoreCase))
                yield break;
            cur = parent;
        }
    }

    static string? TryRel(string root, string abs)
    {
        try
        {
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var a = Path.GetFullPath(abs);
            if (!a.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                return null;
            return a[r.Length..].TrimStart('\\', '/').Replace('\\', '/');
        }
        catch { return null; }
    }

    static FeatureToml? ResolveFeature(WorkspaceTomlDoc? doc, string rel)
    {
        var features = doc?.Workspace?.Features?.Feature;
        if (features is not { Count: > 0 })
            return null;

        var normalized = NormalizePath(rel);
        FeatureToml? best = null;
        var bestLen = -1;
        foreach (var f in features)
        {
            if (f.Paths is not { Count: > 0 })
                continue;
            foreach (var raw in f.Paths)
            {
                var p = NormalizePath(raw);
                if (p.Length == 0)
                    continue;
                if (!normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (p.Length > bestLen)
                {
                    best = f;
                    bestLen = p.Length;
                }
            }
        }

        return best;
    }

    static string BuildFeatureLine(FeatureToml? feature)
    {
        if (feature is null) return "";
        var title = (feature.Title ?? "").Trim();
        var id = (feature.Id ?? "").Trim();
        if (title.Length > 0 && id.Length > 0) return $"Feature: {title} ({id})";
        if (title.Length > 0) return $"Feature: {title}";
        if (id.Length > 0) return $"Feature: {id}";
        return "";
    }

    static List<string> ResolveAdrMap(WorkspaceTomlDoc? doc, string rel)
    {
        var map = doc?.Workspace?.Adr?.Map;
        if (map is not { Count: > 0 })
            return [];

        var normalized = NormalizePath(rel);
        string? bestKey = null;
        var bestLen = -1;
        foreach (var rawKey in map.Keys)
        {
            var k = NormalizePath(rawKey);
            if (k == "*")
            {
                if (bestKey is null)
                {
                    bestKey = rawKey;
                    bestLen = 0;
                }
                continue;
            }

            if (!normalized.StartsWith(k, StringComparison.OrdinalIgnoreCase))
                continue;
            if (k.Length > bestLen)
            {
                bestKey = rawKey;
                bestLen = k.Length;
            }
        }

        if (bestKey is null || !map.TryGetValue(bestKey, out var v))
            return [];

        return ExtractStrings(v)
            .Select(NormalizeDoc)
            .Where(static x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static ReverseAnchor[] LoadReverse(
        WorkspaceTomlDoc? doc,
        string root,
        IReadOnlyList<string> forwardDocs,
        string fileRel)
    {
        var rows = doc?.Workspace?.Correspondence?.CodeAnchors;
        if (rows is not { Count: > 0 })
            return [];

        var fileNorm = NormalizePath(fileRel);
        var forwardSet = new HashSet<string>(forwardDocs.Select(NormalizeDoc), StringComparer.OrdinalIgnoreCase);
        var list = new List<ReverseAnchor>();

        foreach (var row in rows)
        {
            var docPath = NormalizeDoc(row.Doc ?? "");
            if (docPath.Length == 0)
                continue;

            if (!TryParseAnchor(row, out var file, out var lineStart, out var lineEnd, out var member, out var wire))
                continue;

            var fileMatch = string.Equals(NormalizePath(file), fileNorm, StringComparison.OrdinalIgnoreCase);
            var docMatch = forwardSet.Contains(docPath);
            // Include if reverse points at this file, or doc is in forward set (doc→code for current zone).
            if (!fileMatch && !docMatch)
                continue;
            // Prefer rows tied to this file when both present; still show doc-scoped for zone.
            if (!fileMatch && docMatch)
            {
                // keep — reverse anchors for docs of this zone
            }

            var kind = string.IsNullOrWhiteSpace(row.Kind) ? "documents" : row.Kind.Trim();
            list.Add(new ReverseAnchor(
                docPath,
                GuessTitle(docPath),
                "workspace_toml",
                kind,
                file,
                lineStart,
                lineEnd,
                member,
                wire));
        }

        return list.ToArray();
    }

    static bool TryParseAnchor(
        CodeAnchorToml row,
        out string file,
        out int? lineStart,
        out int? lineEnd,
        out string? member,
        out string wire)
    {
        file = "";
        lineStart = row.LineStart;
        lineEnd = row.LineEnd;
        member = string.IsNullOrWhiteSpace(row.MemberKey) ? null : row.MemberKey.Trim();
        wire = "";

        if (!string.IsNullOrWhiteSpace(row.Bracket))
        {
            if (!TryParseBracket(row.Bracket, out file, out var bls, out var ble, out var bm))
                return false;
            lineStart ??= bls;
            lineEnd ??= ble;
            member ??= bm;
        }
        else
        {
            file = NormalizePath(row.File ?? "");
            if (file.Length == 0)
                return false;
        }

        wire = BuildWire(file, lineStart, lineEnd, member);
        return true;
    }

    static bool TryParseBracket(
        string bracket,
        out string file,
        out int? lineStart,
        out int? lineEnd,
        out string? member)
    {
        file = "";
        lineStart = null;
        lineEnd = null;
        member = null;
        var raw = bracket.Trim().Trim('[', ']');
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("F:", StringComparison.OrdinalIgnoreCase))
                file = NormalizePath(part[2..]);
            else if (part.StartsWith("M:", StringComparison.OrdinalIgnoreCase))
                member = part[2..].Trim();
            else if (part.StartsWith("L:", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(part[2..].Trim(), out var ln))
            {
                lineStart = ln;
                lineEnd ??= ln;
            }
        }

        return file.Length > 0;
    }

    static string BuildWire(string file, int? lineStart, int? lineEnd, string? member)
    {
        var parts = new List<string> { $"F:{file.Replace('\\', '/')}" };
        if (member is { Length: > 0 })
            parts.Add($"M:{member}");
        else if (lineStart is int ls)
        {
            parts.Add($"L:{ls}");
            if (lineEnd is int le && le != ls)
                parts.Add($"L2:{le}");
        }

        return "[" + string.Join("; ", parts) + "]";
    }

    static IReadOnlyList<string> ExtractStrings(object? v)
    {
        if (v is null) return [];
        if (v is string s) return string.IsNullOrWhiteSpace(s) ? [] : [s.Trim()];
        if (v is IEnumerable<object> objs)
        {
            var list = new List<string>();
            foreach (var o in objs)
            {
                if (o is string os && os.Trim().Length > 0)
                    list.Add(os.Trim());
                else if (o is not null)
                {
                    var t = o.ToString();
                    if (!string.IsNullOrWhiteSpace(t))
                        list.Add(t.Trim());
                }
            }

            return list;
        }

        var asText = v.ToString();
        return string.IsNullOrWhiteSpace(asText) ? [] : [asText.Trim()];
    }

    static List<string> ExtractLinkedAdrs(string markdown, string currentDoc, string adrRoot)
    {
        var list = new List<string>();
        var current = NormalizeDoc(currentDoc);
        foreach (Match m in MdLinkRegex().Matches(markdown))
        {
            var raw = m.Groups["target"].Value.Trim();
            if (raw.Length == 0) continue;
            if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                continue;

            var hash = raw.IndexOf('#');
            if (hash >= 0) raw = raw[..hash];
            if (raw.Length == 0) continue;

            string? resolved = null;
            var t = raw.Replace('\\', '/');
            if (t.StartsWith(adrRoot, StringComparison.OrdinalIgnoreCase))
                resolved = NormalizeDoc(t);
            else if (t.StartsWith("./", StringComparison.Ordinal)
                     || t.StartsWith("../", StringComparison.Ordinal)
                     || (!t.Contains(':') && !t.StartsWith('/')))
            {
                var lastSlash = current.LastIndexOf('/');
                var baseDir = lastSlash >= 0 ? current[..(lastSlash + 1)] : "";
                var parts = new List<string>();
                foreach (var p in (baseDir + t).Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (p == ".") continue;
                    if (p == "..")
                    {
                        if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                        continue;
                    }
                    parts.Add(p);
                }

                resolved = parts.Count == 0 ? null : string.Join('/', parts);
            }

            if (resolved is null) continue;
            if (!resolved.StartsWith(adrRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(resolved, current, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(resolved);
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string BuildAdrLine(IReadOnlyList<string> docs)
    {
        if (docs.Count == 0) return "";
        var ids = docs.Select(GuessTitle).ToList();
        return ids.Count == 1 ? $"ADR: {ids[0]}" : $"ADR: {ids[0]} (+{ids.Count - 1})";
    }

    static string GuessTitle(string path)
    {
        var m = AdrIdRegex().Match(path.Replace('\\', '/'));
        if (m.Success)
            return $"ADR {m.Groups["id"].Value}";
        return path;
    }

    static string NormalizeAutoInclude(string? raw) =>
        string.Equals((raw ?? "").Trim(), "linked", StringComparison.OrdinalIgnoreCase) ? "linked" : "none";

    static string NormalizeAdrRoot(string? raw)
    {
        var s = (raw ?? "").Trim().Replace('\\', '/');
        if (s.Length == 0) return "docs/adr/";
        if (!s.EndsWith('/')) s += "/";
        return s;
    }

    static string NormalizePath(string raw) => (raw ?? "").Trim().Replace('\\', '/');
    static string NormalizeDoc(string raw) => NormalizePath(raw).TrimStart('/');

    [GeneratedRegex(@"\[[^\]]*\]\((?<target>[^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MdLinkRegex();

    [GeneratedRegex(@"(?:^|/)docs/adr/(?<id>\d{4})-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdrIdRegex();

    // --- toml models (minimal) ---

    sealed class WorkspaceTomlDoc
    {
        public WorkspaceSection? Workspace { get; set; }
    }

    sealed class WorkspaceSection
    {
        public AdrToml? Adr { get; set; }
        public FeaturesToml? Features { get; set; }
        public CorrespondenceToml? Correspondence { get; set; }
    }

    sealed class AdrToml
    {
        public string? AutoInclude { get; set; }
        public int? MaxRelated { get; set; }
        public string? RootDir { get; set; }
        public Dictionary<string, object>? Map { get; set; }
    }

    sealed class FeaturesToml
    {
        public List<FeatureToml> Feature { get; set; } = [];
    }

    sealed class FeatureToml
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public List<string> Paths { get; set; } = [];
        public List<string> Docs { get; set; } = [];
    }

    sealed class CorrespondenceToml
    {
        public List<CodeAnchorToml> CodeAnchors { get; set; } = [];
    }

    sealed class CodeAnchorToml
    {
        public string? Doc { get; set; }
        public string? File { get; set; }
        public string? Bracket { get; set; }
        public int? LineStart { get; set; }
        public int? LineEnd { get; set; }
        public string? Kind { get; set; }
        public string? MemberKey { get; set; }
    }
}
