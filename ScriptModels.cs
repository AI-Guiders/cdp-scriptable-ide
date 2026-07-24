using System.Text.Json;

namespace Cdp.ScriptableIde;

/// <summary>Dispatch CSX facade calls into CDP mounted domains (same CallAsync path as MCP).</summary>
public interface IScriptToolBus
{
    bool IsDryRun { get; }
    IReadOnlyList<ScriptStep> Steps { get; }

    void RecordLocal(
        string domain,
        string underlying,
        IReadOnlyDictionary<string, JsonElement> args,
        string? result,
        bool skippedDryRun = false);

    Task<string> InvokeAsync(
        string domain,
        string underlying,
        IReadOnlyDictionary<string, JsonElement> args,
        CancellationToken cancellationToken = default);
}

public sealed record ScriptStep(
    string Domain,
    string Underlying,
    IReadOnlyDictionary<string, JsonElement> Args,
    string? Result,
    bool SkippedDryRun,
    DateTimeOffset AtUtc);

public sealed record ScriptReport
{
    public required bool Ok { get; init; }
    public required string Mode { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<ScriptStep> Steps { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
    public string? PlanId { get; init; }
    public string? PrimaryRoot { get; init; }
    public string? WorkRoot { get; init; }
    public string? WorktreeDiff { get; init; }
    public string? WorktreeStatus { get; init; }
    public bool? PrimaryClean { get; init; }
    /// <summary>Owning git repo (<c>rev-parse --show-toplevel</c>).</summary>
    public string? GitRoot { get; init; }
    /// <summary>Relative scope under GitRoot (empty = whole repo).</summary>
    public string? PlanScope { get; init; }
    /// <summary>Primary dirty/untracked files copied into worktree under PlanScope.</summary>
    public int? OverlayPathCount { get; init; }
    /// <summary><c>overlap_safe</c> (default) or <c>strict_clean</c>.</summary>
    public string? PromotePolicy { get; init; }
    /// <summary>Tree SHA after overlay (plan delta base).</summary>
    public string? BaseTreeSha { get; init; }
    /// <summary>Captured <see cref="Console.Out"/> during CSX (stdio MCP must stay JSON-only).</summary>
    public string? ConsoleOut { get; init; }
    /// <summary>TEMP scratch dirs deleted after run (hygiene).</summary>
    public IReadOnlyList<string>? ScratchesRemoved { get; init; }
    /// <summary>Structured CSX diagnostics with anchors (prefer over raw Diagnostics strings alone).</summary>
    public IReadOnlyList<CsxDiagnosticProjection.Item>? DiagnosticItems { get; init; }
    /// <summary>Shared <c>evidence/v0</c> locus projection (same schema as build/test).</summary>
    public Cdp.Evidence.EvidenceDocumentDto? Evidence { get; init; }
}
