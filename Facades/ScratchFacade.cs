namespace Cdp.ScriptableIde;

/// <summary>
/// Ephemeral dirs for probes — always under TEMP (never under WorkRoot / csproj tree).
/// ScriptHost deletes registered scratches after CSX run.
/// </summary>
public sealed class ScratchFacade(ScriptToolBus bus, PlanContext plan)
{
    /// <summary>Create <c>%TEMP%/cdp-scratch-{prefix}-{id}</c> and register for auto-cleanup.</summary>
    public string Create(string prefix = "probe")
    {
        var safe = string.Concat((prefix ?? "probe").Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        if (safe.Length == 0)
            safe = "probe";
        var dir = Path.Combine(
            Path.GetTempPath(),
            "cdp-scratch-" + safe + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        bus.RegisterScratch(dir);
        var result = StepResponse.Success("scratch.create", dir, new
        {
            path = dir,
            work_root = plan.WorkRoot,
            note = "TEMP only — do not create probe folders under WorkRoot (SDK compiles **/_*.cs)"
        });
        bus.RecordLocal("scratch", "scratch.create", ScriptArgs.From(new { path = dir, prefix = safe }), result.ToJson());
        return dir;
    }

    public IReadOnlyList<string> Registered => bus.ScratchDirs;
}
