using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cdp.ScriptableIde;

/// <summary>Named refactor intents — Anchor zones; wraps RoslynMCP code actions (CDP-ADR-0008).</summary>
public sealed class RefactorFacade(IScriptToolBus bus, PlanContext plan)
{
    /// <summary><c>Refactor.Extract.Method.From(a).Till(b).Name("X").ApplyAsync()</c></summary>
    public RefactorExtractRoot Extract => new(bus, plan);

    /// <summary>Alias path: <c>Refactor.Method.Extract</c> ≡ <c>Refactor.Extract.Method</c>.</summary>
    public RefactorMethodFacade Method => new(bus, plan);

    public RefactorMoveFacade Move => new(bus, plan);

    /// <summary><c>Refactor.Rename.At(anchor).To("NewName").ApplyAsync()</c></summary>
    public RefactorRenameBuilder Rename => new(bus, plan);

    /// <summary><c>Refactor.Inline.At(anchor).ApplyAsync()</c> — variable / method / temp.</summary>
    public RefactorInlineBuilder Inline => new(bus, plan);

    /// <summary><c>Refactor.Introduce.Local.At(expr).Name("x")</c> / <c>.Param</c>.</summary>
    public RefactorIntroduceRoot Introduce => new(bus, plan);

    /// <summary><c>Refactor.ChangeSignature.At(method).Add(...).Remove(...).Move(...)</c>.</summary>
    public ChangeSignatureBuilder ChangeSignature => new(bus, plan);

    /// <summary><c>Refactor.Change.At(method.ReturnType()).To(Types.Of("X"))</c> — type socket rewrite.</summary>
    public RefactorChangeBuilder Change => new(bus, plan);
}

public sealed class RefactorExtractRoot(IScriptToolBus bus, PlanContext plan)
{
    public ExtractMethodBuilder Method => new(bus, plan);
    public ExtractInterfaceBuilder Interface => new(bus, plan);
    public ExtractBaseBuilder Base => new(bus, plan);
}

public sealed class RefactorMethodFacade(IScriptToolBus bus, PlanContext plan)
{
    public ExtractMethodBuilder Extract => new(bus, plan);
}

public sealed class RefactorMoveFacade(IScriptToolBus bus, PlanContext plan)
{
    public MoveMembersToPartialBuilder MembersToPartial => new(bus, plan);
}

public sealed class MoveMembersToPartialBuilder(IScriptToolBus bus, PlanContext plan)
{
    public MoveMembersToPartialAt At(string anchorTarget) => new(bus, plan, anchorTarget);
    public MoveMembersToPartialAt At(Anchor anchor) => At(anchor.ToWire());
    public MoveMembersToPartialAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class MoveMembersToPartialAt(IScriptToolBus bus, PlanContext plan, string anchorTarget)
{
    private string[]? _members;
    private string? _outputFile;
    private bool _apply = true;
    private bool _addDependentUpon = true;

    /// <summary>Primary: member Anchors (<c>M:</c> names).</summary>
    public MoveMembersToPartialAt Members(params Anchor[] memberAnchors)
    {
        if (!MemberAnchorNames.TryResolve(MoveMembersRunner.Kind, memberAnchors, out var names, out var fail)
            && fail is not null)
            throw new ArgumentException(fail.Error ?? "Members(Anchor) failed");
        _members = names;
        return this;
    }

    /// <summary>Escape: bare member names.</summary>
    public MoveMembersToPartialAt Members(params string[] memberNames)
    {
        _members = memberNames;
        return this;
    }

    public MoveMembersToPartialAt ToFile(string outputFilePath)
    {
        _outputFile = outputFilePath;
        return this;
    }

    public MoveMembersToPartialAt PreviewOnly(bool previewOnly = true)
    {
        _apply = !previewOnly;
        return this;
    }

    public MoveMembersToPartialAt DependentUpon(bool add = true)
    {
        _addDependentUpon = add;
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        MoveMembersRunner.RunAsync(bus, plan, anchorTarget, _members, _outputFile, _apply, _addDependentUpon, ct);
}

internal static class MoveMembersRunner
{
    public const string Kind = "refactor.move_members_to_partial";

    public static async Task<StepResponse> RunAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string anchorTarget,
        string[]? members,
        string? outputFile,
        bool apply,
        bool addDependentUpon,
        CancellationToken ct)
    {
        if (members is not { Length: > 0 })
            return StepResponse.Fail(Kind, "Members(...) is required");
        if (string.IsNullOrWhiteSpace(outputFile))
            return StepResponse.Fail(Kind, "ToFile(...) is required");

        if (!AnchorLocus.TryResolveTypePosition(plan, anchorTarget, Kind, out var file, out var line, out var column,
                out var typeName, out var fail))
            return fail!;

        var sol = AnchorLocus.RequireSolution(plan, Kind, out fail);
        if (fail is not null)
            return fail;

        var outPath = Path.IsPathRooted(outputFile)
            ? Path.GetFullPath(outputFile)
            : Path.GetFullPath(Path.Combine(plan.WorkRoot, outputFile.Replace('/', Path.DirectorySeparatorChar)));
        outPath = plan.Resolve(outPath);

        var raw = await bus.InvokeAsync("roslyn", "roslyn_move_members_to_partial_file", ScriptArgs.From(new
        {
            solution_or_project_path = sol,
            file_path = file,
            line,
            column,
            member_names = members,
            output_file_path = outPath,
            apply,
            add_dependent_upon = addDependentUpon
        }), ct).ConfigureAwait(false);

        var step = FixRunner.NormalizeRoslynMutate(raw, Kind);
        if (!step.Ok
            && (raw.Contains("partial", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("Moved", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("preview", StringComparison.OrdinalIgnoreCase)))
            step = StepResponse.Success(Kind, apply ? "moved" : "preview", new { raw, type = typeName });

        return step.Ok
            ? StepResponse.Success(Kind, step.Summary ?? "ok", new
            {
                type = typeName,
                file,
                output = outPath,
                members,
                apply,
                result = step
            })
            : StepResponse.Fail(Kind, step.Error ?? "move failed", new { raw, type = typeName });
    }
}

public sealed class ExtractMethodBuilder(IScriptToolBus bus, PlanContext plan)
{
    /// <summary>Start of extract zone (statement/expression Anchor).</summary>
    public ExtractMethodFrom From(string anchorTarget) => new(bus, plan, anchorTarget);
    public ExtractMethodFrom From(Anchor anchor) => From(anchor.ToWire());
    public ExtractMethodFrom From(BracketLocate.Span span) => From(BracketLocate.Format(span));

    /// <summary>Escape: single zone wire (L:range or S:). Prefer <see cref="From"/>.<see cref="ExtractMethodFrom.Till"/>.</summary>
    public ExtractMethodAt At(string anchorTarget) => new(bus, plan, anchorTarget);
    public ExtractMethodAt At(Anchor anchor) => At(anchor.ToWire());
    public ExtractMethodAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class ExtractMethodFrom(IScriptToolBus bus, PlanContext plan, string fromTarget)
{
    /// <summary>End of extract zone (inclusive).</summary>
    public ExtractMethodTill Till(string tillTarget) => new(bus, plan, fromTarget, tillTarget);
    public ExtractMethodTill Till(Anchor till) => Till(till.ToWire());
    public ExtractMethodTill Till(BracketLocate.Span till) => Till(BracketLocate.Format(till));
}

public sealed class ExtractMethodTill(IScriptToolBus bus, PlanContext plan, string fromTarget, string tillTarget)
{
    public ExtractMethodApply Name(string methodName) => new(bus, plan, fromTarget, tillTarget, methodName);
}

public sealed class ExtractMethodApply(
    IScriptToolBus bus,
    PlanContext plan,
    string fromTarget,
    string tillTarget,
    string methodName)
{
    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        ExtractMethodRunner.RunAsync(bus, plan, fromTarget, tillTarget, methodName, ct);
}

public sealed class ExtractMethodAt(IScriptToolBus bus, PlanContext plan, string anchorTarget)
{
    /// <summary>Legacy one-shot: zone already in wire.</summary>
    public Task<StepResponse> NameAsync(string methodName, CancellationToken ct = default) =>
        ExtractMethodRunner.RunAsync(bus, plan, anchorTarget, tillTarget: null, methodName, ct);

    public ExtractMethodApply Name(string methodName) =>
        new(bus, plan, anchorTarget, tillTarget: anchorTarget, methodName);
}

public sealed class RefactorRenameBuilder(IScriptToolBus bus, PlanContext plan)
{
    public RefactorRenameAt At(string anchorTarget) => new(bus, plan, anchorTarget);
    public RefactorRenameAt At(Anchor anchor) => At(anchor.ToWire());
    public RefactorRenameAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class RefactorRenameAt(IScriptToolBus bus, PlanContext plan, string anchorTarget)
{
    public RefactorRenameApply To(string newName) => new(bus, plan, anchorTarget, newName);
}

public sealed class RefactorRenameApply(IScriptToolBus bus, PlanContext plan, string anchorTarget, string newName)
{
    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        RefactorRenameRunner.RunAsync(bus, plan, anchorTarget, newName, ct);
}

internal static class RefactorRenameRunner
{
    public const string Kind = "refactor.rename";

    public static async Task<StepResponse> RunAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string anchorTarget,
        string newName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return StepResponse.Fail(Kind, "To(name) is required");

        if (!AnchorLocus.TryResolveFile(plan, anchorTarget, Kind, out var file, out var span, out var fail))
            return fail!;

        var sol = AnchorLocus.RequireSolution(plan, Kind, out fail);
        if (fail is not null)
            return fail;

        int line, column;
        if (BracketSyntaxResolve.TryFindAttachTarget(file, span, out var target, out _))
        {
            var loc = target.Node switch
            {
                MethodDeclarationSyntax m => m.Identifier.GetLocation().GetLineSpan(),
                PropertyDeclarationSyntax p => p.Identifier.GetLocation().GetLineSpan(),
                TypeDeclarationSyntax t => t.Identifier.GetLocation().GetLineSpan(),
                VariableDeclaratorSyntax v => v.Identifier.GetLocation().GetLineSpan(),
                ParameterSyntax p => p.Identifier.GetLocation().GetLineSpan(),
                LocalFunctionStatementSyntax lf => lf.Identifier.GetLocation().GetLineSpan(),
                _ => target.Node.GetLocation().GetLineSpan()
            };
            line = loc.StartLinePosition.Line + 1;
            column = loc.StartLinePosition.Character + 1;
        }
        else if (!AnchorLocus.TryResolveTextRange(plan, anchorTarget, Kind, out file, out var range, out fail))
        {
            return fail!;
        }
        else
        {
            line = range.LineStart;
            column = range.ColumnStart;
        }

        var raw = await bus.InvokeAsync("roslyn", "roslyn_rename", ScriptArgs.From(new
        {
            solution_or_project_path = sol,
            file_path = file,
            line,
            column,
            new_name = newName,
            apply = true
        }), ct).ConfigureAwait(false);

        var step = StepResponse.ParseOrWrap(raw, "roslyn.rename");
        return step.Ok
            ? StepResponse.Success(Kind, $"Renamed to {newName}", new { anchor = anchorTarget, name = newName, result = step })
            : StepResponse.Fail(Kind, step.Error ?? "rename failed", new { raw, anchor = anchorTarget });
    }
}

internal static partial class ExtractMethodRunner
{
    public const string Kind = "refactor.extract_method";

    [GeneratedRegex(@"^(?<idx>\d+)\t(?<title>.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ActionLine();

    public static async Task<StepResponse> RunAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string fromTarget,
        string? tillTarget,
        string methodName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(methodName))
            return StepResponse.Fail(Kind, "method name is required");

        if (!AnchorLocus.TryResolveTextRange(plan, fromTarget, Kind, out var file, out var fromRange, out var fail))
            return fail!;

        BracketSyntaxResolve.TextRange zone;
        if (string.IsNullOrWhiteSpace(tillTarget) || string.Equals(tillTarget, fromTarget, StringComparison.Ordinal))
        {
            zone = fromRange;
        }
        else
        {
            if (!AnchorLocus.TryResolveTextRange(plan, tillTarget!, Kind, out var tillFile, out var tillRange, out fail))
                return fail!;
            if (!string.Equals(file, tillFile, StringComparison.OrdinalIgnoreCase))
                return StepResponse.Fail(Kind, "From/Till must be in the same file", new { from = file, till = tillFile });
            if (!AnchorLocus.TryMergeZones(fromRange, tillRange, Kind, out zone, out fail))
                return fail!;
        }

        var sol = AnchorLocus.RequireSolution(plan, Kind, out fail);
        if (fail is not null)
            return fail;

        var lineStart = zone.LineStart;
        var startCol = zone.ColumnStart;
        var lineEnd = zone.LineEnd;
        var endCol = zone.ColumnEnd;

        var listRaw = await bus.InvokeAsync("roslyn", "roslyn_get_code_actions", ScriptArgs.From(new
        {
            solution_or_project_path = sol,
            file_path = file,
            line = lineStart,
            column = startCol,
            end_line = lineEnd,
            end_column = endCol
        }), ct).ConfigureAwait(false);

        if (!TryPickExtractMethod(listRaw, out var actionIndex, out var title))
            return StepResponse.Fail(Kind, "Extract method not in code actions", new { list = listRaw, zone });

        var applyRaw = await bus.InvokeAsync("roslyn", "roslyn_apply_code_action", ScriptArgs.From(new
        {
            solution_or_project_path = sol,
            file_path = file,
            line = lineStart,
            column = startCol,
            end_line = lineEnd,
            end_column = endCol,
            action_index = actionIndex
        }), ct).ConfigureAwait(false);

        StepResponse? renameStep = null;
        if (!string.Equals(methodName, "NewMethod", StringComparison.Ordinal))
        {
            if (TryFindIdentifier(file, "NewMethod", out var rLine, out var rCol))
            {
                var renameRaw = await bus.InvokeAsync("roslyn", "roslyn_rename", ScriptArgs.From(new
                {
                    solution_or_project_path = sol,
                    file_path = file,
                    line = rLine,
                    column = rCol,
                    new_name = methodName,
                    apply = true
                }), ct).ConfigureAwait(false);
                renameStep = StepResponse.ParseOrWrap(renameRaw, "roslyn.rename");
            }
            else
            {
                renameStep = StepResponse.Fail("roslyn.rename", "NewMethod identifier not found after extract — rename manually");
            }
        }

        var formatRaw = await bus.InvokeAsync("roslyn", "roslyn_format_document", ScriptArgs.From(new
        {
            solution_or_project_path = sol,
            file_path = file,
            apply = true,
            aggressive = true
        }), ct).ConfigureAwait(false);

        return StepResponse.Success(Kind, $"Extracted {methodName}", new
        {
            from = fromTarget,
            till = tillTarget ?? fromTarget,
            zone = new { lineStart, startCol, lineEnd, endCol },
            action_title = title,
            action_index = actionIndex,
            name = methodName,
            apply = StepResponse.ParseOrWrap(applyRaw, "roslyn.apply_code_action"),
            rename = renameStep,
            format = StepResponse.ParseOrWrap(formatRaw, FormatDocumentKind)
        });
    }

    // Wire kind must match FormatDocument.Kind in roslyn-mcp-core (no project ref).
    private const string FormatDocumentKind = "roslyn.format";

    private static bool TryPickExtractMethod(string listRaw, out int index, out string title)
    {
        index = -1;
        title = "";
        var step = StepResponse.ParseOrWrap(listRaw, "roslyn.get_code_actions");
        if (step.Ok && step.Data is { } data)
        {
            if (data.TryGetProperty("actions", out var actions) && actions.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var a in actions.EnumerateArray())
                {
                    var t = a.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
                    if (t.Contains("Extract method", StringComparison.OrdinalIgnoreCase)
                        && !t.Contains("local", StringComparison.OrdinalIgnoreCase))
                    {
                        index = a.TryGetProperty("index", out var idxEl) && idxEl.TryGetInt32(out var i) ? i : -1;
                        title = t;
                        return index >= 0;
                    }
                }
            }
        }

        // Legacy text list fallback
        foreach (Match m in ActionLine().Matches(listRaw))
        {
            var t = m.Groups["title"].Value.Trim();
            if (t.Contains("Extract method", StringComparison.OrdinalIgnoreCase)
                && !t.Contains("local", StringComparison.OrdinalIgnoreCase))
            {
                index = int.Parse(m.Groups["idx"].Value);
                title = t;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindIdentifier(string file, string name, out int line, out int column)
    {
        line = 0;
        column = 0;
        try
        {
            var lines = File.ReadAllLines(file);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var idx = lines[i].IndexOf(name, StringComparison.Ordinal);
                if (idx < 0)
                    continue;
                if (lines[i].Contains("void " + name, StringComparison.Ordinal)
                    || lines[i].Contains(name + "(", StringComparison.Ordinal))
                {
                    line = i + 1;
                    column = idx + 1;
                    return true;
                }
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var idx = lines[i].IndexOf(name, StringComparison.Ordinal);
                if (idx < 0)
                    continue;
                line = i + 1;
                column = idx + 1;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}

// ── Inline / Introduce ──────────────────────────────────────────────────────

public sealed class RefactorInlineBuilder(IScriptToolBus bus, PlanContext plan)
{
    public RefactorInlineAt At(string anchorTarget) => new(bus, plan, anchorTarget);
    public RefactorInlineAt At(Anchor anchor) => At(anchor.ToWire());
    public RefactorInlineAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class RefactorInlineAt(IScriptToolBus bus, PlanContext plan, string anchorTarget)
{
    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        RefactorCodeActionRunner.RunAsync(
            bus, plan, "refactor.inline", anchorTarget,
            [
                new RefactorCodeActionRunner.TitlePick("Inline temporary"),
                new RefactorCodeActionRunner.TitlePick("Inline variable"),
                new RefactorCodeActionRunner.TitlePick("Inline method"),
                new RefactorCodeActionRunner.TitlePick("Inline")
            ],
            ct: ct);
}

public sealed class RefactorIntroduceRoot(IScriptToolBus bus, PlanContext plan)
{
    public IntroduceLocalBuilder Local => new(bus, plan);
    public IntroduceParamBuilder Param => new(bus, plan);
}

public sealed class IntroduceLocalBuilder(IScriptToolBus bus, PlanContext plan)
{
    public IntroduceLocalAt At(string anchorTarget) => new(bus, plan, anchorTarget);
    public IntroduceLocalAt At(Anchor anchor) => At(anchor.ToWire());
    public IntroduceLocalAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class IntroduceLocalAt(IScriptToolBus bus, PlanContext plan, string anchorTarget)
{
    private string? _name;

    public IntroduceLocalAt Name(string name)
    {
        _name = name;
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        RefactorCodeActionRunner.RunAsync(
            bus, plan, "refactor.introduce_local", anchorTarget,
            [
                new RefactorCodeActionRunner.TitlePick("Introduce local"),
                new RefactorCodeActionRunner.TitlePick("Introduce variable")
            ],
            preferredName: _name,
            ct: ct);
}

public sealed class IntroduceParamBuilder(IScriptToolBus bus, PlanContext plan)
{
    public IntroduceParamAt At(string anchorTarget) => new(bus, plan, anchorTarget);
    public IntroduceParamAt At(Anchor anchor) => At(anchor.ToWire());
    public IntroduceParamAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class IntroduceParamAt(IScriptToolBus bus, PlanContext plan, string anchorTarget)
{
    private string? _name;

    public IntroduceParamAt Name(string name)
    {
        _name = name;
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        RefactorCodeActionRunner.RunAsync(
            bus, plan, "refactor.introduce_param", anchorTarget,
            [
                new RefactorCodeActionRunner.TitlePick("Introduce parameter"),
                new RefactorCodeActionRunner.TitlePick("Add parameter")
            ],
            preferredName: _name,
            ct: ct);
}

// ── Extract Interface / Base ────────────────────────────────────────────────

public sealed class ExtractInterfaceBuilder(IScriptToolBus bus, PlanContext plan)
{
    public ExtractTypeAt At(string anchorTarget) => new(bus, plan, "roslyn_generate_interface_from_class",
        "refactor.extract_interface", "interface_name", anchorTarget);
    public ExtractTypeAt At(Anchor anchor) => At(anchor.ToWire());
    public ExtractTypeAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class ExtractBaseBuilder(IScriptToolBus bus, PlanContext plan)
{
    public ExtractTypeAt At(string anchorTarget) => new(bus, plan, "roslyn_generate_base_class_from_class",
        "refactor.extract_base", "base_class_name", anchorTarget);
    public ExtractTypeAt At(Anchor anchor) => At(anchor.ToWire());
    public ExtractTypeAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class ExtractTypeAt(
    IScriptToolBus bus,
    PlanContext plan,
    string underlying,
    string kind,
    string nameArgKey,
    string anchorTarget)
{
    private string? _typeName;
    private string? _outputFile;
    private string[]? _members;

    public ExtractTypeAt Name(string typeName)
    {
        _typeName = typeName;
        return this;
    }

    public ExtractTypeAt ToFile(string outputFilePath)
    {
        _outputFile = outputFilePath;
        return this;
    }

    /// <summary>Primary: member Anchors.</summary>
    public ExtractTypeAt Members(params Anchor[] memberAnchors)
    {
        if (!MemberAnchorNames.TryResolve(kind, memberAnchors, out var names, out var fail) && fail is not null)
            throw new ArgumentException(fail.Error ?? "Members(Anchor) failed");
        _members = names;
        return this;
    }

    /// <summary>Escape: bare names.</summary>
    public ExtractTypeAt Members(params string[] memberNames)
    {
        _members = memberNames;
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        ExtractTypeRunner.RunAsync(bus, plan, underlying, kind, nameArgKey, anchorTarget, _typeName, _outputFile,
            _members, ct);
}

internal static class ExtractTypeRunner
{
    public static async Task<StepResponse> RunAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string underlying,
        string kind,
        string nameArgKey,
        string anchorTarget,
        string? typeName,
        string? outputFile,
        string[]? members,
        CancellationToken ct)
    {
        if (!AnchorLocus.TryResolveTypePosition(plan, anchorTarget, kind, out var file, out var line, out var column,
                out var sourceType, out var fail))
            return fail!;

        var sol = AnchorLocus.RequireSolution(plan, kind, out fail);
        if (fail is not null)
            return fail;

        string? outPath = null;
        if (!string.IsNullOrWhiteSpace(outputFile))
        {
            outPath = Path.IsPathRooted(outputFile)
                ? Path.GetFullPath(outputFile)
                : Path.GetFullPath(Path.Combine(plan.WorkRoot, outputFile.Replace('/', Path.DirectorySeparatorChar)));
            outPath = plan.Resolve(outPath);
        }

        object args;
        if (members is { Length: > 0 } && !string.IsNullOrWhiteSpace(typeName) && outPath is not null)
        {
            args = nameArgKey == "interface_name"
                ? new
                {
                    solution_or_project_path = sol,
                    file_path = file,
                    line,
                    column,
                    interface_name = typeName,
                    output_file_path = outPath,
                    member_names = members
                }
                : new
                {
                    solution_or_project_path = sol,
                    file_path = file,
                    line,
                    column,
                    base_class_name = typeName,
                    output_file_path = outPath,
                    member_names = members
                };
        }
        else if (!string.IsNullOrWhiteSpace(typeName) && outPath is not null)
        {
            args = nameArgKey == "interface_name"
                ? new
                {
                    solution_or_project_path = sol,
                    file_path = file,
                    line,
                    column,
                    interface_name = typeName,
                    output_file_path = outPath
                }
                : new
                {
                    solution_or_project_path = sol,
                    file_path = file,
                    line,
                    column,
                    base_class_name = typeName,
                    output_file_path = outPath
                };
        }
        else if (members is { Length: > 0 } && !string.IsNullOrWhiteSpace(typeName))
        {
            args = nameArgKey == "interface_name"
                ? new
                {
                    solution_or_project_path = sol,
                    file_path = file,
                    line,
                    column,
                    interface_name = typeName,
                    member_names = members
                }
                : new
                {
                    solution_or_project_path = sol,
                    file_path = file,
                    line,
                    column,
                    base_class_name = typeName,
                    member_names = members
                };
        }
        else if (!string.IsNullOrWhiteSpace(typeName))
        {
            args = nameArgKey == "interface_name"
                ? new { solution_or_project_path = sol, file_path = file, line, column, interface_name = typeName }
                : new { solution_or_project_path = sol, file_path = file, line, column, base_class_name = typeName };
        }
        else if (outPath is not null)
        {
            args = new { solution_or_project_path = sol, file_path = file, line, column, output_file_path = outPath };
        }
        else
        {
            args = new { solution_or_project_path = sol, file_path = file, line, column };
        }

        var raw = await bus.InvokeAsync("roslyn", underlying, ScriptArgs.From(args), ct).ConfigureAwait(false);
        var step = FixRunner.NormalizeRoslynMutate(raw, kind);
        if (!step.Ok
            && (raw.Contains("interface", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("class", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("generated", StringComparison.OrdinalIgnoreCase)))
            step = StepResponse.Success(kind, "generated", new { raw, type = sourceType });

        return step.Ok
            ? StepResponse.Success(kind, step.Summary ?? "ok", new
            {
                type = sourceType,
                file,
                output = outPath,
                name = typeName,
                members,
                underlying,
                result = step
            })
            : StepResponse.Fail(kind, step.Error ?? "extract type failed", new { raw, type = sourceType });
    }
}

// ── ChangeSignature ─────────────────────────────────────────────────────────

public enum ParamDirection
{
    In,
    Ref,
    Out,
    InKeyword
}

public sealed class ChangeSignatureBuilder(IScriptToolBus bus, PlanContext plan)
{
    public ChangeSignatureAt At(string anchorTarget) => new(bus, plan, anchorTarget);
    public ChangeSignatureAt At(Anchor anchor) => At(anchor.ToWire());
    public ChangeSignatureAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class ChangeSignatureAt(IScriptToolBus bus, PlanContext plan, string anchorTarget)
{
    private readonly List<ChangeSignatureOp> _ops = [];

    public ChangeSignatureAt Add(string name, TypeIntent type, ParamDirection direction = ParamDirection.In,
        string? defaultValue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(type);
        _ops.Add(new ChangeSignatureOp.Add(name.Trim(), type, direction, defaultValue));
        return this;
    }

    public ChangeSignatureAt Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _ops.Add(new ChangeSignatureOp.Remove(name.Trim()));
        return this;
    }

    public ChangeSignatureMove Move(string name) => new(this, name);

    internal ChangeSignatureAt AddOp(ChangeSignatureOp op)
    {
        _ops.Add(op);
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        ChangeSignatureRunner.RunAsync(bus, plan, anchorTarget, _ops, ct);
}

public sealed class ChangeSignatureMove(ChangeSignatureAt parent, string name)
{
    public ChangeSignatureAt Before(string other)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(other);
        return parent.AddOp(new ChangeSignatureOp.Move(name.Trim(), ChangeSignatureOp.MoveKind.Before, other.Trim(), null));
    }

    public ChangeSignatureAt After(string other)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(other);
        return parent.AddOp(new ChangeSignatureOp.Move(name.Trim(), ChangeSignatureOp.MoveKind.After, other.Trim(), null));
    }

    public ChangeSignatureAt ToPosition(int zeroBasedIndex)
    {
        if (zeroBasedIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(zeroBasedIndex));
        return parent.AddOp(new ChangeSignatureOp.Move(name.Trim(), ChangeSignatureOp.MoveKind.ToPosition, null,
            zeroBasedIndex));
    }
}

internal abstract record ChangeSignatureOp
{
    public sealed record Add(string Name, TypeIntent Type, ParamDirection Direction, string? DefaultValue)
        : ChangeSignatureOp;

    public sealed record Remove(string Name) : ChangeSignatureOp;

    public enum MoveKind
    {
        Before,
        After,
        ToPosition
    }

    public sealed record Move(string Name, MoveKind Kind, string? RelativeTo, int? ToIndex) : ChangeSignatureOp;
}
