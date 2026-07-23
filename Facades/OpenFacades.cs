namespace Cdp.ScriptableIde;

/// <summary>
/// Agent Open Recent — classic IDE list + CIDE-style Anchor→solution bind.
/// <c>Open.At(anchor)</c>, <c>Open.Recent.ListAsync()</c>, <c>Open.Recent.AtAsync(0)</c>, <c>Open.Path(…)</c>.
/// </summary>
public sealed class OpenFacade(IScriptToolBus bus, PlanContext plan)
{
    /// <summary>Classic Open Recent list / open-by-index.</summary>
    public OpenRecentFacade Recent => new(bus, plan);

    /// <summary>CIDE mirror: Anchor (F:…) → detect solution/project → rebind Plan (+ session via bus).</summary>
    public Task<StepResponse> AtAsync(string anchorOrPath, CancellationToken ct = default)
    {
        var span = BracketLocate.Parse(anchorOrPath);
        var path = !string.IsNullOrWhiteSpace(span.File) ? span.File! : anchorOrPath.Trim();
        if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(plan.WorkRoot))
            path = Path.GetFullPath(Path.Combine(plan.WorkRoot, path.Replace('/', Path.DirectorySeparatorChar)));
        else if (!Path.IsPathRooted(path))
            path = Path.GetFullPath(path);
        else
            path = Path.GetFullPath(path);
        return PathAsync(path, ct);
    }

    public Task<StepResponse> AtAsync(Anchor anchor, CancellationToken ct = default) =>
        AtAsync(anchor.ToWire(), ct);

    public Task<StepResponse> AtAsync(BracketLocate.Span span, CancellationToken ct = default) =>
        AtAsync(BracketLocate.Format(span), ct);

    /// <summary>Open path (.sln/.csproj/file/dir) — detect project, rebind Plan, push Recent, notify session.</summary>
    public async Task<StepResponse> PathAsync(string path, CancellationToken ct = default)
    {
        const string kind = "open.path";
        if (!ProjectBind.TryDetect(path, out var bind, out var err))
            return StepResponse.Fail(kind, $"detect failed: {err}", new { path });

        plan.Rebind(bind.Root, bind.AnchorPath, bind.Language);
        OpenRecentStore.Push(bind.AnchorPath, bind.Root, bind.Kind, bind.Language);

        string? sessionRaw = null;
        try
        {
            sessionRaw = await bus.InvokeAsync("cdp", "session_open", ScriptArgs.From(new
            {
                path = bind.AnchorPath,
                root = bind.Root,
                kind = bind.Kind,
                language = bind.Language
            }), ct).ConfigureAwait(false);
        }
        catch
        {
            // local Plan rebind is enough for this script when host has no cdp domain
        }

        var step = StepResponse.Success(kind, $"Opened {bind.Kind}: {Path.GetFileName(bind.AnchorPath)}", new
        {
            root = bind.Root,
            path = bind.AnchorPath,
            kind = bind.Kind,
            language = bind.Language,
            plan_work_root = plan.WorkRoot,
            plan_solution = plan.SolutionOrProjectPath,
            session = sessionRaw
        });
        bus.RecordLocal("open", kind, ScriptArgs.From(new { path = bind.AnchorPath }), step.ToJson());
        return step;
    }
}

public sealed class OpenRecentFacade(IScriptToolBus bus, PlanContext plan)
{
    /// <summary>List recent projects/solutions (most recent first).</summary>
    public Task<StepResponse> ListAsync(int take = 12, CancellationToken ct = default)
    {
        _ = ct;
        var items = OpenRecentStore.List(take);
        var step = StepResponse.Success("open.recent", $"{items.Count} recent", new
        {
            items = items.Select((e, i) => new
            {
                index = i,
                path = e.Path,
                root = e.Root,
                kind = e.Kind,
                language = e.Language,
                opened_utc = e.OpenedUtc
            })
        });
        bus.RecordLocal("open", "open.recent", ScriptArgs.From(new { take }), step.ToJson());
        return Task.FromResult(step);
    }

    /// <summary>Open recent entry by 0-based index (0 = last opened). Alias of classic Open Recent[0].</summary>
    public Task<StepResponse> AtAsync(int index, CancellationToken ct = default)
    {
        var hit = OpenRecentStore.TryGet(index);
        if (hit is null)
            return Task.FromResult(StepResponse.Fail("open.recent", $"no recent at index {index}"));
        return new OpenFacade(bus, plan).PathAsync(hit.Path, ct);
    }
}
