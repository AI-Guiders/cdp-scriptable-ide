namespace Cdp.ScriptableIde;

public sealed class VerifyFacade(IScriptToolBus bus)
{
    public async Task<StepResponse> BuildStructuredAsync(string projectOrSolutionPath, CancellationToken ct = default)
    {
        var raw = await bus.InvokeAsync("build", "build_structured", ScriptArgs.From(new
        {
            solution_path = projectOrSolutionPath
        }), ct).ConfigureAwait(false);
        return WrapBuildish(raw, "verify.build");
    }

    public async Task<StepResponse> RunTestsAsync(string projectOrSolutionPath, CancellationToken ct = default)
    {
        var raw = await bus.InvokeAsync("build", "run_tests", ScriptArgs.From(new
        {
            solution_path = projectOrSolutionPath
        }), ct).ConfigureAwait(false);
        return WrapBuildish(raw, "verify.tests");
    }

    public async Task<StepResponse> DiagnosticsAsync(
        string solutionOrProjectPath,
        string? filePath = null,
        CancellationToken ct = default)
    {
        var raw = await bus.InvokeAsync("roslyn", "roslyn_get_diagnostics", ScriptArgs.From(new
        {
            solution_or_project_path = solutionOrProjectPath,
            file_path = filePath
        }), ct).ConfigureAwait(false);
        return StepResponse.ParseOrWrap(raw, "roslyn.get_diagnostics");
    }

    /// <summary>Named AEE rung — maps to build/test/diagnostics for MVP.</summary>
    public Task<StepResponse> RungAsync(string rungId, string projectOrSolutionPath, CancellationToken ct = default) =>
        rungId switch
        {
            "build.solution" or "build" => BuildStructuredAsync(projectOrSolutionPath, ct),
            "test.full" or "test" => RunTestsAsync(projectOrSolutionPath, ct),
            "diagnostics" or "diag" => DiagnosticsAsync(projectOrSolutionPath, null, ct),
            _ => throw new ArgumentException($"Unknown verify rung: {rungId}. Known: build.solution, test.full, diagnostics")
        };

    private static StepResponse WrapBuildish(string raw, string kind)
    {
        var parsed = StepResponse.ParseOrWrap(raw, kind);
        if (parsed.Kind == kind || parsed.Ok || parsed.Error != "non_step_response")
            return parsed with { Kind = parsed.Kind is "non_step_response" or null ? kind : parsed.Kind };
        // Legacy text from build domain — treat as ok payload with raw summary
        var looksFail = raw.Contains("error", StringComparison.OrdinalIgnoreCase)
                        && (raw.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
                            || raw.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase));
        return looksFail
            ? StepResponse.Fail(kind, "build/test reported failure", new { raw })
            : StepResponse.Success(kind, "ok", new { raw });
    }
}
