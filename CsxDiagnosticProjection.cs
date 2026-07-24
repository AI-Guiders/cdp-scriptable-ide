using System.Text.Json;
using Cdp.Evidence;
using Microsoft.CodeAnalysis;

namespace Cdp.ScriptableIde;

/// <summary>
/// Project Roslyn CSX diagnostics to agent-friendly anchors (not raw <c>(line,col)</c> dump alone).
/// Also folds into shared <see cref="EvidencePreprocess"/> <c>evidence/v0</c>.
/// </summary>
public static class CsxDiagnosticProjection
{
    public const string SchemaVersion = "csx_diag/v0";
    public const string ScriptFileToken = "<csx>";

    public sealed record Item(
        string Id,
        string Severity,
        string Message,
        int? Line,
        int? Column,
        string? Anchor,
        string? Hint);

    public static IReadOnlyList<Item> FromDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var list = new List<Item>();
        foreach (var d in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            int? line = null;
            int? col = null;
            string? anchor = null;
            var loc = d.Location;
            if (loc.IsInSource)
            {
                var span = loc.GetLineSpan();
                line = span.StartLinePosition.Line + 1;
                col = span.StartLinePosition.Character + 1;
                anchor = Anchor.File(ScriptFileToken).Line(line.Value).ToWire();
            }

            var message = d.GetMessage();
            list.Add(new Item(
                d.Id,
                "error",
                message,
                line,
                col,
                anchor,
                EvidenceHints.ForCode(d.Id, message) ?? SuggestHintLegacy(d)));
        }

        return list;
    }

    public static EvidenceDocument ToEvidence(IReadOnlyList<Item> items) =>
        EvidencePreprocess.FromCsxItems(items.Select(i => (i.Id, i.Severity, i.Message, i.Line, i.Column, i.Anchor, i.Hint)));

    public static string[] ToLegacyStrings(IReadOnlyList<Item> items) =>
        items.Select(i =>
            i.Anchor is { Length: > 0 }
                ? $"{i.Id} {i.Anchor}: {i.Message}"
                : $"{i.Id}: {i.Message}").ToArray();

    public static string ToJson(IReadOnlyList<Item> items) =>
        JsonSerializer.Serialize(new
        {
            schema = SchemaVersion,
            count = items.Count,
            items,
            evidence = EvidencePreprocess.ToDto(ToEvidence(items))
        });

    private static string? SuggestHintLegacy(Diagnostic d)
    {
        var msg = d.GetMessage();
        if (d.Id is "CS0103" && msg.Contains("Report", StringComparison.Ordinal))
            return "No Report global — return a value or use Help.Toc(); Explore via Symbol/SemanticMap.";
        if (d.Id is "CS1061" && msg.Contains("SymbolFacade", StringComparison.Ordinal))
            return "Symbol has Named/FindUsages — not SearchAsync. Try Help.Of(\"Symbol\").";
        if (d.Id is "CS1061" && msg.Contains("SemanticMapFacade", StringComparison.Ordinal))
            return "SemanticMap.Explore(anchor) requires NamedCodeAnchor/CodeAnchor. Try Help.Of(\"SemanticMap\").";
        if (d.Id is "CS1501" or "CS1503")
            return "Check overloads via Help.Of(\"…\") before inventing args.";
        return null;
    }
}
