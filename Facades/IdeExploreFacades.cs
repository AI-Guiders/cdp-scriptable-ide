using System.Text.Json;

namespace Cdp.ScriptableIde;

/// <summary>
/// Explore by name — not SearchAsync. Canonical: <c>Symbol.Named("T").In("F.cs")</c>
/// then FindUsages / pass to <see cref="SemanticMapFacade.Explore"/>.
/// </summary>
public sealed class SymbolFacade(IScriptToolBus bus, PlanContext plan)
{
    /// <summary>Resolve by name — no manual line/column. Chain <c>.In(file)</c> then Resolve/FindUsages.</summary>
    public NamedCodeAnchor Named(string symbolName) => new(bus, plan, symbolName);

    public Task<string> EnqueueFindUsagesAsync(
        CodeAnchor anchor,
        string? stageTitle = null,
        CancellationToken ct = default)
    {
        var resolved = Resolve(anchor);
        var job = JsonSerializer.Serialize(new
        {
            kind = "find_usages",
            file_path = resolved.FilePath,
            line = resolved.Line,
            column = resolved.Column,
            solution_or_project_path = resolved.SolutionOrProjectPath
        });
        return bus.InvokeAsync("cdp_work", "stage_enqueue", ScriptArgs.From(new
        {
            title = stageTitle ?? $"FindUsages {Path.GetFileName(resolved.FilePath)}",
            job_json = job,
            start_job = true
        }), ct);
    }

    public async Task<string> FindUsagesAsync(CodeAnchor anchor, CancellationToken ct = default)
    {
        var resolved = Resolve(anchor);
        RequirePositional(resolved);
        var raw = await bus.InvokeAsync("roslyn", "roslyn_find_usages", ScriptArgs.From(new
        {
            solution_or_project_path = resolved.SolutionOrProjectPath,
            file_path = resolved.FilePath,
            line = resolved.Line!.Value,
            column = resolved.Column!.Value
        }), ct).ConfigureAwait(false);
        return IdeReportBuilder.FromFindUsages(resolved, raw).ToJson();
    }

    private CodeAnchor Resolve(CodeAnchor a)
    {
        var sol = a.SolutionOrProjectPath ?? plan.SolutionOrProjectPath;
        return a with { SolutionOrProjectPath = sol };
    }

    private static void RequirePositional(CodeAnchor a)
    {
        if (a.Line is null or < 1 || a.Column is null or < 1)
            throw new ArgumentException("FindUsages requires CodeAnchor with line and column >= 1 (use Symbol.Named).");
        if (string.IsNullOrWhiteSpace(a.SolutionOrProjectPath))
            throw new ArgumentException("solution_or_project_path required (cdp_open .sln/.csproj or pass on CodeAnchor).");
    }
}

/// <summary>Fluent named resolve → <see cref="CodeAnchor"/> / FindUsages.</summary>
public sealed class NamedCodeAnchor(IScriptToolBus bus, PlanContext plan, string symbolName)
{
    private string? _file;

    public NamedCodeAnchor In(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _file = filePath.Trim();
        return this;
    }

    public CodeAnchor Resolve()
    {
        if (string.IsNullOrWhiteSpace(_file))
            throw new InvalidOperationException("Symbol.Named(...).In(file) required before Resolve.");
        return CodeAnchorResolve.Named(plan, _file!, symbolName);
    }

    public Task<string> FindUsagesAsync(CancellationToken ct = default) =>
        new SymbolFacade(bus, plan).FindUsagesAsync(Resolve(), ct);
}

/// <summary>
/// Related/scene map around an anchor. Canonical:
/// <c>await SemanticMap.Explore(Symbol.Named("X").In("X.cs")).Mode("related").GetSceneAsync()</c>
/// — not a free-form Search.
/// </summary>
public sealed class SemanticMapFacade(IScriptToolBus bus, PlanContext plan)
{
    /// <summary>Explore surface: wide strokes around an anchor (preset / caps / kinds). Prefer GetSceneAsync.</summary>
    public SemanticMapExplore Explore(CodeAnchor anchor) => new(bus, plan, anchor);

    public SemanticMapExplore Explore(NamedCodeAnchor named) => Explore(named.Resolve());

    public Task<string> EnqueueAroundAsync(
        CodeAnchor anchor,
        string mode = "related",
        string? stageTitle = null,
        string? preset = null,
        int? maxRelated = null,
        CancellationToken ct = default)
    {
        var resolved = Resolve(anchor);
        var jobObj = new Dictionary<string, object?>
        {
            ["kind"] = "semantic_map",
            ["mode"] = mode,
            ["file_path"] = resolved.FilePath,
            ["solution_or_project_path"] = resolved.SolutionOrProjectPath
        };
        if (resolved.Line is { } ln) jobObj["line"] = ln;
        if (resolved.Column is { } col) jobObj["column"] = col;
        if (!string.IsNullOrWhiteSpace(preset)) jobObj["preset"] = preset;
        if (maxRelated is { } mr) jobObj["max_related"] = mr;
        var job = JsonSerializer.Serialize(jobObj);
        return bus.InvokeAsync("cdp_work", "stage_enqueue", ScriptArgs.From(new
        {
            title = stageTitle ?? $"SemanticMap {Path.GetFileName(resolved.FilePath)}",
            job_json = job,
            start_job = true
        }), ct);
    }

    public Task<string> AroundAsync(
        CodeAnchor anchor,
        string mode = "related",
        CancellationToken ct = default) =>
        AroundAsync(anchor, mode, preset: null, maxRelated: null, includeKinds: null, excludeKinds: null, ct);

    public async Task<string> AroundAsync(
        CodeAnchor anchor,
        string mode,
        string? preset,
        int? maxRelated,
        IReadOnlyList<string>? includeKinds,
        IReadOnlyList<string>? excludeKinds,
        CancellationToken ct = default)
    {
        var resolved = Resolve(anchor);
        if (string.IsNullOrWhiteSpace(resolved.SolutionOrProjectPath))
            throw new ArgumentException("solution_or_project_path required (cdp_open .sln/.csproj or pass on CodeAnchor).");
        var scriptArgs = ScriptArgs.From(new
        {
            solution_or_project_path = resolved.SolutionOrProjectPath,
            file_path = resolved.FilePath,
            mode,
            line = resolved.Line,
            column = resolved.Column
        });
        if (!string.IsNullOrWhiteSpace(preset))
            scriptArgs["preset"] = JsonSerializer.SerializeToElement(preset);
        if (maxRelated is { } mr)
            scriptArgs["max_related"] = JsonSerializer.SerializeToElement(mr);
        if (includeKinds is { Count: > 0 })
            scriptArgs["include_kinds"] = JsonSerializer.SerializeToElement(includeKinds);
        if (excludeKinds is { Count: > 0 })
            scriptArgs["exclude_kinds"] = JsonSerializer.SerializeToElement(excludeKinds);
        scriptArgs = scriptArgs
            .Where(kv => kv.Value.ValueKind is not JsonValueKind.Null)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var raw = await bus.InvokeAsync("roslyn", "roslyn_get_workspace_navigation_context", scriptArgs, ct)
            .ConfigureAwait(false);
        return IdeReportBuilder.FromSemanticMapRelated(resolved, raw, mode).ToJson();
    }

    /// <summary>Raw Map JSON (for scene merge) — not IdeReport.</summary>
    internal async Task<string> AroundRawAsync(
        CodeAnchor anchor,
        string mode,
        string? preset,
        int? maxRelated,
        IReadOnlyList<string>? includeKinds,
        IReadOnlyList<string>? excludeKinds,
        CancellationToken ct)
    {
        var resolved = Resolve(anchor);
        if (string.IsNullOrWhiteSpace(resolved.SolutionOrProjectPath))
            throw new ArgumentException("solution_or_project_path required (cdp_open .sln/.csproj or pass on CodeAnchor).");
        var scriptArgs = ScriptArgs.From(new
        {
            solution_or_project_path = resolved.SolutionOrProjectPath,
            file_path = resolved.FilePath,
            mode,
            line = resolved.Line,
            column = resolved.Column
        });
        if (!string.IsNullOrWhiteSpace(preset))
            scriptArgs["preset"] = JsonSerializer.SerializeToElement(preset);
        if (maxRelated is { } mr)
            scriptArgs["max_related"] = JsonSerializer.SerializeToElement(mr);
        if (includeKinds is { Count: > 0 })
            scriptArgs["include_kinds"] = JsonSerializer.SerializeToElement(includeKinds);
        if (excludeKinds is { Count: > 0 })
            scriptArgs["exclude_kinds"] = JsonSerializer.SerializeToElement(excludeKinds);
        scriptArgs = scriptArgs
            .Where(kv => kv.Value.ValueKind is not JsonValueKind.Null)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return await bus.InvokeAsync("roslyn", "roslyn_get_workspace_navigation_context", scriptArgs, ct)
            .ConfigureAwait(false);
    }

    private CodeAnchor Resolve(CodeAnchor a) =>
        a with { SolutionOrProjectPath = a.SolutionOrProjectPath ?? plan.SolutionOrProjectPath };
}

/// <summary>Fluent Map wide-strokes builder (not usages alone — use <see cref="WithUsages"/> for scene).</summary>
public sealed class SemanticMapExplore(IScriptToolBus bus, PlanContext plan, CodeAnchor anchor)
{
    private string _mode = "related";
    private string? _preset;
    private int? _maxRelated;
    private IReadOnlyList<string>? _includeKinds;
    private IReadOnlyList<string>? _excludeKinds;
    private bool _withUsages;

    public SemanticMapExplore Mode(string mode)
    {
        _mode = string.IsNullOrWhiteSpace(mode) ? "related" : mode.Trim();
        return this;
    }

    public SemanticMapExplore Preset(string preset)
    {
        _preset = preset;
        return this;
    }

    public SemanticMapExplore MaxRelated(int maxRelated)
    {
        _maxRelated = maxRelated;
        return this;
    }

    public SemanticMapExplore IncludeKinds(params string[] kinds)
    {
        _includeKinds = kinds;
        return this;
    }

    public SemanticMapExplore ExcludeKinds(params string[] kinds)
    {
        _excludeKinds = kinds;
        return this;
    }

    /// <summary>Include FindUsages detail in one scene report (needs positional anchor — use Symbol.Named).</summary>
    public SemanticMapExplore WithUsages(bool include = true)
    {
        _withUsages = include;
        return this;
    }

    public Task<string> GetAsync(CancellationToken ct = default) =>
        new SemanticMapFacade(bus, plan).AroundAsync(anchor, _mode, _preset, _maxRelated, _includeKinds, _excludeKinds, ct);

    public async Task<string> GetSceneAsync(CancellationToken ct = default)
    {
        var mapFacade = new SemanticMapFacade(bus, plan);
        var mapRaw = await mapFacade.AroundRawAsync(anchor, _mode, _preset, _maxRelated, _includeKinds, _excludeKinds, ct)
            .ConfigureAwait(false);
        string? usagesRaw = null;
        if (_withUsages)
        {
            var resolved = anchor with { SolutionOrProjectPath = anchor.SolutionOrProjectPath ?? plan.SolutionOrProjectPath };
            if (resolved.Line is null or < 1 || resolved.Column is null or < 1)
                throw new ArgumentException("WithUsages requires positional CodeAnchor — use Symbol.Named(...).In(file).Resolve().");
            if (string.IsNullOrWhiteSpace(resolved.SolutionOrProjectPath))
                throw new ArgumentException("solution_or_project_path required (cdp_open).");
            usagesRaw = await bus.InvokeAsync("roslyn", "roslyn_find_usages", ScriptArgs.From(new
            {
                solution_or_project_path = resolved.SolutionOrProjectPath,
                file_path = resolved.FilePath,
                line = resolved.Line!.Value,
                column = resolved.Column!.Value
            }), ct).ConfigureAwait(false);
        }

        var positional = anchor with { SolutionOrProjectPath = anchor.SolutionOrProjectPath ?? plan.SolutionOrProjectPath };
        return IdeReportBuilder.FromExploreScene(positional, mapRaw, usagesRaw, _mode).ToJson();
    }

    public Task<string> EnqueueAsync(string? stageTitle = null, CancellationToken ct = default) =>
        new SemanticMapFacade(bus, plan).EnqueueAroundAsync(anchor, _mode, stageTitle, _preset, _maxRelated, ct);
}

public sealed class CorrespondenceFacade
{
    public Task<string> FindAsync(CodeAnchor anchor, string? workspaceRootHint = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(IdeReport.Correspondence(anchor, workspaceRootHint).ToJson());
    }
}

public sealed class WorkFacade(IScriptToolBus bus)
{
    public Task<string> StageGetAsync(Guid stageId, CancellationToken ct = default) =>
        bus.InvokeAsync("cdp_work", "stage_get", ScriptArgs.From(new { stage_id = stageId.ToString("D") }), ct);

    public Task<string> StageSetStatusAsync(Guid stageId, string status, CancellationToken ct = default) =>
        bus.InvokeAsync("cdp_work", "stage_set_status", ScriptArgs.From(new
        {
            stage_id = stageId.ToString("D"),
            status
        }), ct);

    public Task<string> StatusAsync(CancellationToken ct = default) =>
        bus.InvokeAsync("cdp_work", "status", ScriptArgs.From(new { }), ct);
}
