namespace Cdp.ScriptableIde;

/// <summary>Language-agnostic doc intent (MLP DocModel). Wire via projection.</summary>
public sealed class DocModel
{
    public string? Summary { get; set; }
    public List<DocParam> Params { get; } = [];
    public string? Returns { get; set; }
    public List<DocThrows> Throws { get; } = [];
    public string? Remarks { get; set; }
    public List<string> SeeAlso { get; } = [];
    public string? Examples { get; set; }
    /// <summary>Escape hatch: full doc body; when set, structured fields are ignored for projection.</summary>
    public string? Raw { get; set; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Raw)
        && string.IsNullOrWhiteSpace(Summary)
        && Params.Count == 0
        && string.IsNullOrWhiteSpace(Returns)
        && Throws.Count == 0
        && string.IsNullOrWhiteSpace(Remarks)
        && SeeAlso.Count == 0
        && string.IsNullOrWhiteSpace(Examples);
}

public sealed record DocParam(string Name, string Text);
public sealed record DocThrows(string? Type, string Text);
