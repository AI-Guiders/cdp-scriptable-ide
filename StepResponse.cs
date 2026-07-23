using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cdp.ScriptableIde;

/// <summary>
/// Structured outcome of one tool/mutate step (wire JSON over MCP/bus).
/// Not <see cref="IdeReport"/> (explore gaze) and not <see cref="ScriptReport"/> (whole CSX run).
/// </summary>
public sealed record StepResponse
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, StepResponseJson.Options);

    public override string ToString() => ToJson();

    public static StepResponse Success(string kind, string? summary = null, object? data = null) => new()
    {
        Ok = true,
        Kind = kind,
        Summary = summary,
        Data = data is null ? null : JsonSerializer.SerializeToElement(data, StepResponseJson.Options)
    };

    public static StepResponse Fail(string kind, string error, object? data = null) => new()
    {
        Ok = false,
        Kind = kind,
        Error = error,
        Summary = error,
        Data = data is null ? null : JsonSerializer.SerializeToElement(data, StepResponseJson.Options)
    };

    /// <summary>Parse StepResponse JSON; non-JSON / legacy text → wrap as fail with detail in data.</summary>
    public static StepResponse ParseOrWrap(string raw, string kindIfWrap)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Fail(kindIfWrap, "empty response");

        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<StepResponse>(raw, StepResponseJson.Options);
                if (parsed is { Kind: not null })
                    return parsed;
            }
            catch (JsonException)
            {
                // fall through — wrap
            }
        }

        return Fail(kindIfWrap, "non_step_response", new { raw });
    }
}

internal static class StepResponseJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}
