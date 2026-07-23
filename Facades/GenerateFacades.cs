namespace Cdp.ScriptableIde;

/// <summary>W2 Generate intents — overrides / ctor / Equals over RoslynMCP generators.</summary>
public sealed class GenerateFacade(IScriptToolBus bus, PlanContext plan)
{
    public GenerateMemberOp Overrides => new(bus, plan, "roslyn_generate_overrides", "generate.overrides");
    public GenerateMemberOp Constructor => new(bus, plan, "roslyn_generate_constructor_from_members", "generate.constructor");
    public GenerateMemberOp Equals => new(bus, plan, "roslyn_generate_equals_gethashcode", "generate.equals");

    /// <summary>W3 — TestMethod entity (assertions accumulate until Apply).</summary>
    public TestMethodEntity TestMethod(string sutBracket, string name) => new(bus, plan, sutBracket, name);

    public TestMethodEntity TestMethod(Bracket sut, string name) => TestMethod(sut.ToWire(), name);

    /// <summary>W3 — scaffold empty test class file next to SUT (xUnit projection).</summary>
    public Task<StepResponse> UnitTestAsync(
        string sutBracket,
        string? testClassName = null,
        string? outputFilePath = null,
        CancellationToken ct = default) =>
        UnitTestScaffold.RunAsync(bus, plan, sutBracket, testClassName, outputFilePath, TestFrameworkPolicy.Detect, null, ensurePackage: true, ct);

    public Task<StepResponse> UnitTestAsync(
        Bracket sut,
        string? testClassName = null,
        string? outputFilePath = null,
        CancellationToken ct = default) =>
        UnitTestAsync(sut.ToWire(), testClassName, outputFilePath, ct);
}

public sealed class GenerateMemberOp(IScriptToolBus bus, PlanContext plan, string underlying, string kind)
{
    public GenerateMemberAt At(string bracketTarget) => new(bus, plan, underlying, kind, bracketTarget);
    public GenerateMemberAt At(Bracket bracket) => At(bracket.ToWire());
    public GenerateMemberAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class GenerateMemberAt(
    IScriptToolBus bus,
    PlanContext plan,
    string underlying,
    string kind,
    string bracketTarget)
{
    private string[]? _members;
    private bool _insertIntoFile = true;

    public GenerateMemberAt Members(params string[] memberNames)
    {
        _members = memberNames;
        return this;
    }

    /// <summary>false = return generated text only (Roslyn preview).</summary>
    public GenerateMemberAt PreviewOnly(bool previewOnly = true)
    {
        _insertIntoFile = !previewOnly;
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        GenerateMemberRunner.RunAsync(bus, plan, underlying, kind, bracketTarget, _members, _insertIntoFile, ct);
}

internal static class GenerateMemberRunner
{
    public static async Task<StepResponse> RunAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string underlying,
        string kind,
        string bracketTarget,
        string[]? members,
        bool insertIntoFile,
        CancellationToken ct)
    {
        if (!BracketLocus.TryResolveTypePosition(plan, bracketTarget, kind, out var file, out var line, out var column,
                out var typeName, out var fail))
            return fail!;

        var sol = BracketLocus.RequireSolution(plan, kind, out fail);
        if (fail is not null)
            return fail;

        object args = members is { Length: > 0 }
            ? new
            {
                solution_or_project_path = sol,
                file_path = file,
                line,
                column,
                member_names = members,
                insert_into_file = insertIntoFile
            }
            : new
            {
                solution_or_project_path = sol,
                file_path = file,
                line,
                column,
                insert_into_file = insertIntoFile
            };

        var raw = await bus.InvokeAsync("roslyn", underlying, ScriptArgs.From(args), ct).ConfigureAwait(false);
        var apply = FixRunner.NormalizeRoslynMutate(raw, kind);
        if (!apply.Ok && raw.Contains("override", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("constructor", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("Equals", StringComparison.Ordinal)
            || raw.Contains("GetHashCode", StringComparison.Ordinal))
        {
            // Some generators return plain text on success without StepResponse
            apply = StepResponse.Success(kind, insertIntoFile ? "inserted" : "preview", new { raw, type = typeName });
        }
        else if (!apply.Ok && insertIntoFile
                 && (raw.Contains("inserted", StringComparison.OrdinalIgnoreCase)
                     || raw.Contains("updated", StringComparison.OrdinalIgnoreCase)
                     || raw.Contains("Wrote", StringComparison.OrdinalIgnoreCase)))
        {
            apply = StepResponse.Success(kind, "inserted", new { raw, type = typeName });
        }

        return apply.Ok
            ? StepResponse.Success(kind, apply.Summary ?? "ok", new
            {
                type = typeName,
                file,
                line,
                column,
                members,
                insert_into_file = insertIntoFile,
                underlying,
                result = apply
            })
            : StepResponse.Fail(kind, apply.Error ?? "generate failed", new
            {
                type = typeName,
                file,
                raw,
                result = apply
            });
    }
}
