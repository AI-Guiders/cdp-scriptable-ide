namespace Cdp.ScriptableIde;

/// <summary>CSX brand: <c>Projects.Scene/Create/List</c> — TFM via <see cref="TfmPolicy"/>.</summary>
public sealed class ProjectsFacade(ScriptToolBus bus, PlanContext plan)
{
    /// <summary>Project map before create — curated templates + session + existing (prefer over inventing files).</summary>
    public Task<StepResponse> SceneAsync(
        string? root = null,
        bool includeInstalled = false,
        int maxExisting = ProjectScene.MaxExistingDefault,
        int maxInstalled = ProjectScene.MaxInstalledDefault,
        CancellationToken ct = default) =>
        ProjectOps.SceneAsync(bus, plan, root, includeInstalled, maxExisting, maxInstalled, ct);

    public Task<StepResponse> CreateAsync(
        string outputDir,
        string? name = null,
        string template = "console",
        TfmPolicy tfmPolicy = TfmPolicy.PreferMostUsed,
        string? tfm = null,
        EnginePolicy enginePolicy = EnginePolicy.PreferMostUsed,
        string? engines = null,
        bool force = false,
        CancellationToken ct = default) =>
        ProjectOps.CreateAsync(bus, plan, outputDir, name, template, tfmPolicy, tfm, enginePolicy, engines, force, ct);

    public Task<StepResponse> ListAsync(string? root = null, CancellationToken ct = default) =>
        ProjectOps.ListAsync(bus, plan, root, ct);

    /// <summary>Add csproj to solution (session sln or <paramref name="solution"/>).</summary>
    public Task<StepResponse> AddToSlnAsync(
        string projectPath,
        string? solution = null,
        bool inRoot = false,
        string? solutionFolder = null,
        CancellationToken ct = default) =>
        SolutionOps.AddProjectAsync(bus, plan, projectPath, solution, inRoot, solutionFolder, ct);
}
