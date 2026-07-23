namespace Cdp.ScriptableIde;

/// <summary>
/// Internal MCP projection (file + line/col). Agent surface = <see cref="Anchor"/>; prefer <see cref="FromAnchor"/>.
/// </summary>
public sealed record CodeAnchor(
    string FilePath,
    int? Line = null,
    int? Column = null,
    string? SolutionOrProjectPath = null)
{
    [Obsolete("Agent surface: use Anchor. Harness projects Anchor→L/C internally via FromAnchor.")]
    public static CodeAnchor At(string filePath, int line, int column, string? solutionOrProjectPath = null) =>
        new(filePath, line, column, solutionOrProjectPath);

    public static CodeAnchor File(string filePath, string? solutionOrProjectPath = null) =>
        new(filePath, null, null, solutionOrProjectPath);

    /// <summary>Internal: mutate <see cref="Anchor"/> → MCP line/col projection.</summary>
    public static CodeAnchor FromAnchor(PlanContext plan, Anchor anchor, string? solutionOrProjectPath = null) =>
        CodeAnchorResolve.FromAnchor(plan, anchor, solutionOrProjectPath);
}
