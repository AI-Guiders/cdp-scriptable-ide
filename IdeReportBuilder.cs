using System.Text.Json;

namespace Cdp.ScriptableIde;

/// <summary>Build compact IdeReport from Roslyn MCP JSON payloads.</summary>
public static class IdeReportBuilder
{
    public const int MaxHighlights = 8;

    public static IdeReport FromSemanticMapRelated(CodeAnchor anchor, string roslynJson, string mode)
    {
        var highlights = new List<IdeReportHighlight>();
        try
        {
            using var doc = JsonDocument.Parse(roslynJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (highlights.Count >= MaxHighlights) break;
                    var path = item.TryGetProperty("path", out var p) ? p.GetString()
                        : item.TryGetProperty("file_path", out var fp) ? fp.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var kind = item.TryGetProperty("kind", out var k) ? k.GetString()
                        : item.TryGetProperty("relation_kind", out var rk) ? rk.GetString()
                        : "related";
                    highlights.Add(new IdeReportHighlight { Path = path!, Why = kind ?? "related" });
                }
            }
            else if (root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in nodes.EnumerateArray())
                {
                    if (highlights.Count >= MaxHighlights) break;
                    var path = node.TryGetProperty("path", out var p) ? p.GetString()
                        : node.TryGetProperty("file_path", out var fp) ? fp.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var label = node.TryGetProperty("label", out var l) ? l.GetString() : "node";
                    highlights.Add(new IdeReportHighlight { Path = path!, Why = label ?? "node" });
                }
            }
        }
        catch (JsonException)
        {
            // fall through with empty highlights
        }

        var summary = highlights.Count == 0
            ? $"Semantic map ({mode}) returned no neighbors for {Path.GetFileName(anchor.FilePath)}."
            : $"Semantic map ({mode}): {highlights.Count} related path(s) around {Path.GetFileName(anchor.FilePath)}.";

        return new IdeReport
        {
            Kind = "semantic_map",
            Available = true,
            Anchor = IdeReportAnchor.From(anchor),
            Summary = summary,
            Highlights = highlights,
            Next = highlights.Count == 0
                ?
                [
                    "Verify solution_or_project_path and that the file is in the loaded project.",
                    "Try mode=subgraph or a peers_only preset via bare get_workspace_navigation_context."
                ]
                :
                [
                    "Open the top highlight and go_to_definition / find_usages.",
                    "analysis_scene feature=correspondence for ADR↔code when workspace.toml present."
                ]
        };
    }

    public static IdeReport FromFindUsages(CodeAnchor anchor, string roslynPayload)
    {
        var highlights = new List<IdeReportHighlight>();
        TryParseFindUsagesJson(roslynPayload, highlights, MaxHighlights);
        if (highlights.Count == 0)
            TryParseFindUsagesMarkdown(roslynPayload, highlights, MaxHighlights);

        return new IdeReport
        {
            Kind = "find_usages",
            Available = true,
            Anchor = IdeReportAnchor.From(anchor),
            Summary = highlights.Count == 0
                ? $"No usages found at {Path.GetFileName(anchor.FilePath)}:{anchor.Line}."
                : $"Find usages: {highlights.Count} location(s) (capped at {MaxHighlights}).",
            Highlights = highlights,
            Next = highlights.Count == 0
                ? ["Confirm line/column on a symbol name.", "Try Symbol.Named(name).In(file)."]
                : ["Inspect first usage paths.", "SemanticMap.Explore for structural neighbors."]
        };
    }

    /// <summary>One-gaze: Map wide strokes + optional usages detail.</summary>
    public static IdeReport FromExploreScene(
        CodeAnchor anchor,
        string semanticMapJson,
        string? usagesPayload,
        string mode)
    {
        var mapAll = new List<IdeReportHighlight>();
        CollectMapHighlights(semanticMapJson, mapAll, max: 64);
        var usageAll = new List<IdeReportHighlight>();
        if (!string.IsNullOrWhiteSpace(usagesPayload))
        {
            TryParseFindUsagesJson(usagesPayload, usageAll, max: 64);
            if (usageAll.Count == 0)
                TryParseFindUsagesMarkdown(usagesPayload!, usageAll, max: 64);
        }

        var kindBits = mapAll
            .GroupBy(h => h.Why, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}×{g.Count()}")
            .ToList();
        var kinds = kindBits.Count == 0 ? "none" : string.Join(", ", kindBits);
        var usageFiles = usageAll.Select(h => h.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var summary = usagesPayload is null
            ? $"Explore scene ({mode}): map {mapAll.Count} ({kinds}) around {Path.GetFileName(anchor.FilePath)}."
            : $"Explore scene ({mode}): map {mapAll.Count} ({kinds}); usages {usageAll.Count} locs / {usageFiles} files — {Path.GetFileName(anchor.FilePath)}.";

        var highlights = MergeSceneHighlights(mapAll, usageAll, MaxHighlights);
        return new IdeReport
        {
            Kind = "explore_scene",
            Available = true,
            Anchor = IdeReportAnchor.From(anchor),
            Summary = summary,
            Highlights = highlights,
            Next = highlights.Count == 0
                ?
                [
                    "Verify cdp_open and that the file is in the project.",
                    "Use Symbol.Named before WithUsages()."
                ]
                :
                [
                    "Zoom a highlight with Symbol.Named / FindUsages.",
                    "Mutate via Anchor.File().Member — same name, no column."
                ]
        };
    }

    private static List<IdeReportHighlight> MergeSceneHighlights(
        List<IdeReportHighlight> map,
        List<IdeReportHighlight> usages,
        int max)
    {
        var list = new List<IdeReportHighlight>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(IdeReportHighlight h)
        {
            if (list.Count >= max) return;
            var key = h.Path + "|" + h.Why;
            if (!seen.Add(key)) return;
            list.Add(h);
        }

        var mi = 0;
        var ui = 0;
        while (list.Count < max && (mi < map.Count || ui < usages.Count))
        {
            if (mi < map.Count) Add(map[mi++]);
            if (ui < usages.Count && list.Count < max) Add(usages[ui++]);
        }

        return list;
    }

    private static void CollectMapHighlights(string roslynJson, List<IdeReportHighlight> highlights, int max)
    {
        try
        {
            using var doc = JsonDocument.Parse(roslynJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (highlights.Count >= max) break;
                    var path = item.TryGetProperty("path", out var p) ? p.GetString()
                        : item.TryGetProperty("file_path", out var fp) ? fp.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var kind = item.TryGetProperty("kind", out var k) ? k.GetString()
                        : item.TryGetProperty("relation_kind", out var rk) ? rk.GetString()
                        : "related";
                    highlights.Add(new IdeReportHighlight { Path = path!, Why = kind ?? "related" });
                }
            }
            else if (root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in nodes.EnumerateArray())
                {
                    if (highlights.Count >= max) break;
                    var path = node.TryGetProperty("path", out var p) ? p.GetString()
                        : node.TryGetProperty("file_path", out var fp) ? fp.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var label = node.TryGetProperty("label", out var l) ? l.GetString() : "node";
                    highlights.Add(new IdeReportHighlight { Path = path!, Why = label ?? "node" });
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private static void TryParseFindUsagesJson(string payload, List<IdeReportHighlight> highlights, int max)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            IEnumerable<JsonElement> locs = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : root.TryGetProperty("locations", out var locsEl) && locsEl.ValueKind == JsonValueKind.Array
                    ? locsEl.EnumerateArray()
                    : root.TryGetProperty("usages", out var u) && u.ValueKind == JsonValueKind.Array
                        ? u.EnumerateArray()
                        : root.TryGetProperty("references", out var r) && r.ValueKind == JsonValueKind.Array
                            ? r.EnumerateArray()
                            : [];

            foreach (var loc in locs)
            {
                if (highlights.Count >= max) break;
                var path = loc.TryGetProperty("file_path", out var fp) ? fp.GetString()
                    : loc.TryGetProperty("path", out var p) ? p.GetString()
                    : loc.TryGetProperty("uri", out var uri) ? uri.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(path)) continue;
                var line = loc.TryGetProperty("line", out var ln) && ln.TryGetInt32(out var li) ? li : (int?)null;
                var why = line is { } l ? $"usage L{l}" : "usage";
                highlights.Add(new IdeReportHighlight { Path = path!, Why = why });
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>RoslynMCP FindUsages text: lines like <c>D:\path\File.cs:12:3</c>.</summary>
    private static void TryParseFindUsagesMarkdown(string payload, List<IdeReportHighlight> highlights, int max)
    {
        foreach (var rawLine in payload.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (highlights.Count >= max) break;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("Definition:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Total:", StringComparison.OrdinalIgnoreCase))
                continue;
            var lastColon = line.LastIndexOf(':');
            if (lastColon <= 0) continue;
            var midColon = line.LastIndexOf(':', lastColon - 1);
            if (midColon <= 0) continue;
            var pathPart = line[..midColon];
            var linePart = line[(midColon + 1)..lastColon];
            if (!int.TryParse(linePart, out var locLine)) continue;
            if (!pathPart.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !pathPart.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
                continue;
            highlights.Add(new IdeReportHighlight { Path = pathPart, Why = $"usage L{locLine}" });
        }
    }
}
