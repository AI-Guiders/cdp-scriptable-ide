namespace Cdp.ScriptableIde;

public sealed class DebugFacade(IScriptToolBus bus)
{
    public Task<string> PingAsync(CancellationToken ct = default) =>
        bus.InvokeAsync("debug", "debug_ping", ScriptArgs.From(new { }), ct);

    /// <summary>Persist breakpoints for a target (same as debug_set_breakpoints).</summary>
    public Task<string> SetBreakpointOnLineAsync(
        string workspacePath,
        string targetPath,
        string filePath,
        int line,
        string? condition = null,
        CancellationToken ct = default)
    {
        object bp = condition is null
            ? new { file_path = filePath, line }
            : new { file_path = filePath, line, condition };
        return bus.InvokeAsync("debug", "debug_set_breakpoints", ScriptArgs.From(new
        {
            workspace_path = workspacePath,
            target_path = targetPath,
            breakpoints = new[] { bp }
        }), ct);
    }

    public Task<string> LaunchAsync(
        string workspacePath,
        string targetPath,
        string? cwd = null,
        CancellationToken ct = default) =>
        bus.InvokeAsync("debug", "debug_launch", ScriptArgs.From(new
        {
            workspace_path = workspacePath,
            target_path = targetPath,
            cwd
        }), ct);

    public Task<string> ContinueAsync(CancellationToken ct = default) =>
        bus.InvokeAsync("debug", "debug_continue", ScriptArgs.From(new { }), ct);

    public Task<string> StopAsync(CancellationToken ct = default) =>
        bus.InvokeAsync("debug", "debug_stop", ScriptArgs.From(new { }), ct);

    public Task<string> StopContextAsync(CancellationToken ct = default) =>
        bus.InvokeAsync("debug", "debug_stop_context", ScriptArgs.From(new { }), ct);
}
