using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cdp.ScriptableIde;

/// <summary>Compact IDE report for one-gaze consumption (not raw Roslyn dump).</summary>
public sealed class IdeReport
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("available")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Available { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonPropertyName("anchor")]
    public required IdeReportAnchor Anchor { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("highlights")]
    public required IReadOnlyList<IdeReportHighlight> Highlights { get; init; }

    [JsonPropertyName("next")]
    public required IReadOnlyList<string> Next { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, IdeReportJson.Options);

    public static IdeReport CorrespondenceStub(CodeAnchor anchor) => new()
    {
        Kind = "correspondence",
        Available = false,
        Reason = "needs_cide_host",
        Anchor = IdeReportAnchor.From(anchor),
        Summary = "Correspondence (doc↔code) is not available in standalone CDP; needs CDP-in-CIDE.",
        Highlights = [],
        Next = ["Open CIDE host when Correspondence MCP is wired.", "Meanwhile use SemanticMap.Explore(...).WithUsages().GetSceneAsync()."]
    };
}

public sealed class IdeReportAnchor
{
    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; init; }

    [JsonPropertyName("column")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Column { get; init; }

    public static IdeReportAnchor From(CodeAnchor a) => new()
    {
        File = a.FilePath,
        Line = a.Line,
        Column = a.Column
    };
}

public sealed class IdeReportHighlight
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("why")]
    public required string Why { get; init; }
}

internal static class IdeReportJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
