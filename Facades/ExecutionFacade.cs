namespace Cdp.ScriptableIde;

/// <summary>Binds a TOML step registry for this plan (same idea as CIDE LSP settings.toml).</summary>
public sealed class ExecEnvironment(ScriptGlobals root)
{
    public ExecutionConfiguration Configuration { get; } = new(root);
}

public sealed class ExecutionConfiguration(ScriptGlobals root)
{
    /// <summary>Path to TOML file, or inline TOML text containing [steps.*].</summary>
    public void Set(string tomlOrPath)
    {
        root.ExecutionRegistry.LoadFromToml(tomlOrPath, root.Plan);
        var args = ScriptArgs.From(new { source = root.ExecutionRegistry.Source, step_count = root.ExecutionRegistry.Steps.Count });
        root.Bus.RecordLocal("execution", "configuration.set", args,
            StepResponse.Success("execution.configuration.set", "loaded",
                new { source = root.ExecutionRegistry.Source, steps = root.ExecutionRegistry.Steps.Keys }).ToJson());
    }
}

/// <summary>CI-like named steps resolved from ExecEnvironment.Configuration.</summary>
public sealed class ExecutionFacade(ScriptGlobals root)
{
    public Task<StepResponse> PreConditionAsync(string stepId, CancellationToken ct = default) =>
        RunAsync(stepId, phase: "pre_condition", ct);

    public Task<StepResponse> PostConditionAsync(string stepId, CancellationToken ct = default) =>
        RunAsync(stepId, phase: "post_condition", ct);

    public Task<StepResponse> RunAsync(string stepId, CancellationToken ct = default) =>
        RunAsync(stepId, phase: "run", ct);

    private async Task<StepResponse> RunAsync(string stepId, string phase, CancellationToken ct)
    {
        // Fix C: worktree run_plan must not invoke arbitrary executables until runner policy exists.
        if (root.Plan.IsWorktree)
        {
            throw new InvalidOperationException(
                "Execution.* process invoke is disabled in cdp_csx_run_plan worktree until ExecutionPolicy (runner) is wired. " +
                "Use Mutate.Fs / Roslyn / Git / Verify domain tools only. Configuration.Set remains allowed.");
        }

        var def = root.ExecutionRegistry.GetRequired(stepId);
        var exe = Expand(def.Executable, root.Plan);
        var args = def.Arguments.Select(a => Expand(a, root.Plan)).ToArray();
        var cwd = string.IsNullOrWhiteSpace(def.WorkingDirectory)
            ? root.Plan.WorkRoot
            : Expand(def.WorkingDirectory!, root.Plan);

        var recordArgs = ScriptArgs.From(new
        {
            step_id = stepId,
            phase,
            executable = exe,
            arguments = args,
            working_directory = cwd,
            config = root.ExecutionRegistry.Source
        });

        var kind = $"execution.{phase}";
        if (root.Bus.IsDryRun)
        {
            var dry = StepResponse.Success(kind, "dry_run", new { dry_run = true, step_id = stepId, phase });
            root.Bus.RecordLocal("execution", phase, recordArgs, dry.ToJson(), skippedDryRun: true);
            return dry;
        }

        var (code, stdout, stderr) = await ProcessUtil.RunAsync(exe, args, cwd, env: null, ct).ConfigureAwait(false);
        var data = new
        {
            step_id = stepId,
            phase,
            exit_code = code,
            stdout = Truncate(stdout, 4000),
            stderr = Truncate(stderr, 2000)
        };
        var result = code == 0
            ? StepResponse.Success(kind, "ok", data)
            : StepResponse.Fail(kind, $"exit={code}", data);
        root.Bus.RecordLocal("execution", phase, recordArgs, result.ToJson(), skippedDryRun: false);
        if (code != 0)
            throw new InvalidOperationException(
                $"Execution.{phase}('{stepId}') failed exit={code}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}".Trim());
        return result;
    }

    private static string Expand(string value, PlanContext plan) =>
        value
            .Replace("{{work_root}}", plan.WorkRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{{primary_root}}", plan.PrimaryRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{{plan_id}}", plan.PlanId, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
