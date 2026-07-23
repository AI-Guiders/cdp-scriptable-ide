namespace Cdp.ScriptableIde;

public sealed class GitFacade(IScriptToolBus bus)
{
    public Task<string> StatusAsync(string workspacePath, CancellationToken ct = default) =>
        bus.InvokeAsync("git", "git_status", ScriptArgs.From(new { workspace_path = workspacePath }), ct);

    /// <summary>Compact SCM scene (ADR 0178): dirty counts, ahead/behind, submodule map — prefer before Status dump.</summary>
    public Task<string> SceneAsync(
        string workspacePath,
        IReadOnlyList<string>? roots = null,
        bool includeSubmodules = true,
        bool probeSubmoduleDirty = true,
        int? maxRoots = null,
        int? maxSubmodules = null,
        CancellationToken ct = default) =>
        bus.InvokeAsync("git", "git_scene", ScriptArgs.From(new
        {
            workspace_path = workspacePath,
            roots,
            include_submodules = includeSubmodules,
            probe_submodule_dirty = probeSubmoduleDirty,
            max_roots = maxRoots,
            max_submodules = maxSubmodules
        }), ct);

    /// <summary>Diff scene: omit path → file list+numstat; path= → structured hunks (prefer over DiffAsync dump).</summary>
    public Task<string> DiffSceneAsync(
        string workspacePath,
        string? path = null,
        bool staged = false,
        bool includeUntracked = true,
        int? maxFiles = null,
        int? maxHunks = null,
        int? maxHunkLines = null,
        CancellationToken ct = default) =>
        bus.InvokeAsync("git", "git_diff_scene", ScriptArgs.From(new
        {
            workspace_path = workspacePath,
            path,
            staged,
            include_untracked = includeUntracked,
            max_files = maxFiles,
            max_hunks = maxHunks,
            max_hunk_lines = maxHunkLines
        }), ct);

    public Task<string> DiffAsync(string workspacePath, string? path = null, bool staged = false, CancellationToken ct = default) =>
        bus.InvokeAsync("git", "git_diff", ScriptArgs.From(new
        {
            workspace_path = workspacePath,
            path,
            staged
        }), ct);

    public Task<string> PreflightAsync(string workspacePath, bool staged = false, CancellationToken ct = default) =>
        bus.InvokeAsync("git", "git_preflight", ScriptArgs.From(new
        {
            workspace_path = workspacePath,
            staged
        }), ct);

    public Task<string> CommitAsync(
        string workspacePath,
        string message,
        IReadOnlyList<string>? paths = null,
        CancellationToken ct = default) =>
        bus.InvokeAsync("git", "git_commit", ScriptArgs.From(new
        {
            workspace_path = workspacePath,
            message,
            paths
        }), ct);

    /// <summary>Related multi-root commit: slices with per-root paths (no add -A). Prefer over N× CommitAsync.</summary>
    public Task<string> CommitAsync(
        string message,
        IReadOnlyList<object> slices,
        CancellationToken ct = default) =>
        bus.InvokeAsync("git", "git_commit", ScriptArgs.From(new { message, slices }), ct);

    public Task<string> PushAsync(
        string workspacePath,
        string? remote = null,
        string? branch = null,
        bool dryRun = false,
        CancellationToken ct = default) =>
        bus.InvokeAsync("git", "git_push", ScriptArgs.From(new
        {
            workspace_path = workspacePath,
            remote,
            branch,
            dry_run = dryRun
        }), ct);

    /// <summary>Related multi-root push: slices=[{root,...}].</summary>
    public Task<string> PushAsync(IReadOnlyList<object> slices, bool dryRun = false, CancellationToken ct = default) =>
        bus.InvokeAsync("git", "git_push", ScriptArgs.From(new { slices, dry_run = dryRun }), ct);
}
