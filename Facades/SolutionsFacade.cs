namespace Cdp.ScriptableIde;

/// <summary>CSX brand: <c>Solutions.Create/List/ListProjects/Add/Remove</c>.</summary>
public sealed class SolutionsFacade(ScriptToolBus bus, PlanContext plan)
{
    public Task<StepResponse> CreateAsync(
        string outputDir,
        string? name = null,
        bool force = false,
        CancellationToken ct = default) =>
        SolutionOps.CreateAsync(bus, plan, outputDir, name, force, open: false, ct);

    public Task<StepResponse> ListAsync(string? root = null, CancellationToken ct = default) =>
        SolutionOps.ListAsync(bus, plan, root, ct);

    public Task<StepResponse> ListProjectsAsync(string? solution = null, CancellationToken ct = default) =>
        SolutionOps.ListProjectsAsync(bus, plan, solution, ct);

    public Task<StepResponse> AddAsync(
        string projectPath,
        string? solution = null,
        bool inRoot = false,
        string? solutionFolder = null,
        CancellationToken ct = default) =>
        SolutionOps.AddProjectAsync(bus, plan, projectPath, solution, inRoot, solutionFolder, ct);

    public Task<StepResponse> RemoveAsync(
        string projectPath,
        string? solution = null,
        CancellationToken ct = default) =>
        SolutionOps.RemoveProjectAsync(bus, plan, projectPath, solution, ct);
}
