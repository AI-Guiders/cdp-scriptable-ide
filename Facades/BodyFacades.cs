using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cdp.ScriptableIde;

/// <summary>Body entity ops — AddCondition / AddLoop (intent); syntax is projection tax.</summary>
public sealed class BodyFacade(IScriptToolBus bus, PlanContext plan)
{
    public BodyAt At(string anchorTarget) => new(bus, plan, anchorTarget);
    public BodyAt At(Anchor anchor) => At(anchor.ToWire());
    public BodyAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class BodyAt(IScriptToolBus bus, PlanContext plan, string anchorTarget)
{
    public BodyConditionEntity AddCondition() => new(bus, plan, anchorTarget);
    public BodyLoopEntity AddLoop() => new(bus, plan, anchorTarget);

    /// <summary>Insert a <see cref="Declare"/> local/const into the method body (default Append).</summary>
    public BodyDeclareEntity AddDeclare(DeclareBuilder declare) => new(bus, plan, anchorTarget, declare);
}

/// <summary><see cref="Declare"/> → method body statement.</summary>
public sealed class BodyDeclareEntity(
    IScriptToolBus bus,
    PlanContext plan,
    string anchorTarget,
    DeclareBuilder declare)
{
    private BodyInsert _insert = BodyInsert.Append;

    public BodyDeclareEntity AtStart()
    {
        _insert = BodyInsert.AtStart;
        return this;
    }

    public BodyDeclareEntity Insert(BodyInsert where)
    {
        _insert = where;
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default)
    {
        DeclareArrange arranged;
        try
        {
            arranged = (DeclareArrange)declare.ToArrange();
        }
        catch (Exception ex)
        {
            return Task.FromResult(StepResponse.Fail(BodyRunner.DeclareKind, ex.Message));
        }

        var lang = plan.Language ?? "csharp";
        if (!DeclareProjection.TryProject(lang, arranged, out var line, out var err))
            return Task.FromResult(StepResponse.Fail(BodyRunner.DeclareKind, err ?? "declare project failed"));

        return BodyRunner.InsertIntoMethodBodyAsync(
            bus, plan, BodyRunner.DeclareKind, anchorTarget, line + "\n", _insert,
            new { declare = arranged.Kind, local = arranged.Local, insert = _insert.ToString() });
    }
}

/// <summary>Where to splice into the method body (default = append before <c>}</c>).</summary>
public enum BodyInsert
{
    /// <summary>Before closing brace — after existing statements (declare-then-branch).</summary>
    Append = 0,

    /// <summary>Right after opening brace — guards / early setup.</summary>
    AtStart = 1
}

/// <summary>if / guard — <see cref="When"/> + <see cref="Then"/> (+ optional <see cref="Else"/>).</summary>
public sealed class BodyConditionEntity(IScriptToolBus bus, PlanContext plan, string anchorTarget)
{
    private string? _whenWire;
    private PredicateIntent? _whenPred;
    private string? _thenWire;
    private List<StmtIntent>? _thenStmts;
    private string? _elseWire;
    private List<StmtIntent>? _elseStmts;
    private BodyInsert _insert = BodyInsert.Append;

    /// <summary>Wire escape — prefer <see cref="When(PredicateIntent)"/>.</summary>
    public BodyConditionEntity When(string predicateWire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(predicateWire);
        _whenWire = predicateWire.Trim();
        _whenPred = null;
        return this;
    }

    public BodyConditionEntity When(PredicateIntent predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _whenPred = predicate;
        _whenWire = null;
        return this;
    }

    /// <summary>Wire escape — prefer <see cref="Then(StmtIntent, StmtIntent[])"/> (no <c>;</c> in intent).</summary>
    public BodyConditionEntity Then(string bodyWire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyWire);
        _thenWire = bodyWire.Trim();
        _thenStmts = null;
        return this;
    }

    public BodyConditionEntity Then(StmtIntent first, params StmtIntent[] more)
    {
        ArgumentNullException.ThrowIfNull(first);
        _thenStmts = [first, .. more];
        _thenWire = null;
        return this;
    }

    /// <summary>Block of statements — at least one.</summary>
    public BodyConditionEntity Then(StmtIntent[] stmts)
    {
        ArgumentNullException.ThrowIfNull(stmts);
        if (stmts.Length == 0)
            throw new ArgumentException("Then requires at least one Stmt", nameof(stmts));
        _thenStmts = [.. stmts];
        _thenWire = null;
        return this;
    }

    public BodyConditionEntity Else(string bodyWire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyWire);
        _elseWire = bodyWire.Trim();
        _elseStmts = null;
        return this;
    }

    public BodyConditionEntity Else(StmtIntent first, params StmtIntent[] more)
    {
        ArgumentNullException.ThrowIfNull(first);
        _elseStmts = [first, .. more];
        _elseWire = null;
        return this;
    }

    /// <summary>Insert after <c>{</c> (guards). Default is <see cref="BodyInsert.Append"/>.</summary>
    public BodyConditionEntity AtStart()
    {
        _insert = BodyInsert.AtStart;
        return this;
    }

    public BodyConditionEntity Insert(BodyInsert where)
    {
        _insert = where;
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default)
    {
        var lang = plan.Language ?? "csharp";
        string? when = _whenWire;
        if (_whenPred is not null)
        {
            if (!PredicateProjection.TryProject(lang, _whenPred, out when, out var err))
                return Task.FromResult(StepResponse.Fail(BodyRunner.ConditionKind, err ?? "predicate project failed"));
        }

        if (!TryResolveBody(lang, _thenWire, _thenStmts, "Then", out var then, out var thenErr))
            return Task.FromResult(StepResponse.Fail(BodyRunner.ConditionKind, thenErr!));

        string? @else = null;
        if (_elseWire is not null || _elseStmts is not null)
        {
            if (!TryResolveBody(lang, _elseWire, _elseStmts, "Else", out @else, out var elseErr))
                return Task.FromResult(StepResponse.Fail(BodyRunner.ConditionKind, elseErr!));
        }

        return BodyRunner.ApplyConditionAsync(bus, plan, anchorTarget, when, then, @else, _insert, ct);
    }

    private static bool TryResolveBody(
        string language,
        string? wire,
        List<StmtIntent>? stmts,
        string label,
        out string body,
        out string? error)
    {
        body = "";
        error = null;
        if (stmts is { Count: > 0 })
            return StmtProjection.TryProjectBlock(language, stmts, out body, out error);
        if (!string.IsNullOrWhiteSpace(wire))
        {
            body = wire!;
            return true;
        }

        error = $"{label} requires Stmt… or wire escape";
        return false;
    }
}

/// <summary>
/// Loop — one exclusive axis: <see cref="OnCollection"/> | <see cref="PreCondition"/> |
/// <see cref="PostCondition"/> | <see cref="WithCounter"/>; then <see cref="Body"/>.
/// </summary>
public sealed class BodyLoopEntity(IScriptToolBus bus, PlanContext plan, string anchorTarget)
{
    private enum AxisKind { None, Collection, Pre, Post, Counter }

    private AxisKind _axis;
    private string? _collection;
    private string _item = "item";
    private string? _pre;
    private string? _post;
    private string? _init;
    private string? _cond;
    private string? _step;
    private string? _body;
    private List<StmtIntent>? _bodyStmts;
    private BodyInsert _insert = BodyInsert.Append;

    public BodyLoopEntity OnCollection(string collectionWire, string itemLocal = "item")
    {
        SetAxis(AxisKind.Collection);
        _collection = collectionWire;
        _item = string.IsNullOrWhiteSpace(itemLocal) ? "item" : itemLocal.Trim();
        return this;
    }

    public BodyLoopEntity PreCondition(string predicateWire)
    {
        SetAxis(AxisKind.Pre);
        _pre = predicateWire;
        return this;
    }

    public BodyLoopEntity PreCondition(PredicateIntent predicate)
    {
        SetAxis(AxisKind.Pre);
        _pre = ProjectPred(plan, predicate);
        return this;
    }

    public BodyLoopEntity PostCondition(string predicateWire)
    {
        SetAxis(AxisKind.Post);
        _post = predicateWire;
        return this;
    }

    public BodyLoopEntity PostCondition(PredicateIntent predicate)
    {
        SetAxis(AxisKind.Post);
        _post = ProjectPred(plan, predicate);
        return this;
    }

    public BodyLoopEntity WithCounter(string initWire, string conditionWire, string stepWire)
    {
        SetAxis(AxisKind.Counter);
        _init = initWire;
        _cond = conditionWire;
        _step = stepWire;
        return this;
    }

    public BodyLoopEntity WithCounter(string initWire, PredicateIntent condition, string stepWire)
    {
        SetAxis(AxisKind.Counter);
        _init = initWire;
        _cond = ProjectPred(plan, condition);
        _step = stepWire;
        return this;
    }

    public BodyLoopEntity Body(string bodyWire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyWire);
        _body = bodyWire.Trim();
        _bodyStmts = null;
        return this;
    }

    public BodyLoopEntity Body(StmtIntent first, params StmtIntent[] more)
    {
        ArgumentNullException.ThrowIfNull(first);
        _bodyStmts = [first, .. more];
        _body = null;
        return this;
    }

    /// <summary>Insert after <c>{</c>. Default is <see cref="BodyInsert.Append"/>.</summary>
    public BodyLoopEntity AtStart()
    {
        _insert = BodyInsert.AtStart;
        return this;
    }

    public BodyLoopEntity Insert(BodyInsert where)
    {
        _insert = where;
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default)
    {
        var lang = plan.Language ?? "csharp";
        string? body = _body;
        if (_bodyStmts is { Count: > 0 })
        {
            if (!StmtProjection.TryProjectBlock(lang, _bodyStmts, out body, out var err))
                return Task.FromResult(StepResponse.Fail(BodyRunner.LoopKind, err ?? "body stmt project failed"));
        }

        return BodyRunner.ApplyLoopAsync(
            bus, plan, anchorTarget, _axis.ToString(), _collection, _item, _pre, _post, _init, _cond, _step, body,
            _insert, ct);
    }

    private void SetAxis(AxisKind kind)
    {
        if (_axis != AxisKind.None && _axis != kind)
            throw new InvalidOperationException(
                $"AddLoop already set axis {_axis}; one exclusive axis (OnCollection|PreCondition|PostCondition|WithCounter).");
        _axis = kind;
    }

    private static string ProjectPred(PlanContext plan, PredicateIntent predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var lang = plan.Language ?? "csharp";
        if (!PredicateProjection.TryProject(lang, predicate, out var wire, out var err))
            throw new InvalidOperationException(err ?? "predicate project failed");
        return wire;
    }
}

internal static class BodyRunner
{
    public const string ConditionKind = "body.add_condition";
    public const string LoopKind = "body.add_loop";
    public const string DeclareKind = "body.add_declare";

    public static Task<StepResponse> InsertIntoMethodBodyAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string kind,
        string anchorTarget,
        string snippet,
        BodyInsert insert,
        object meta) =>
        InsertIntoMethodBodyCoreAsync(bus, plan, kind, anchorTarget, snippet, insert, meta);

    public static Task<StepResponse> ApplyConditionAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string anchorTarget,
        string? when,
        string? then,
        string? @else,
        BodyInsert insert,
        CancellationToken ct)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(when))
            return Task.FromResult(StepResponse.Fail(ConditionKind, "When(predicate) is required"));
        if (string.IsNullOrWhiteSpace(then))
            return Task.FromResult(StepResponse.Fail(ConditionKind, "Then(body) is required"));

        var block = ProjectCondition(when!.Trim(), then!.Trim(), @else?.Trim());
        return InsertIntoMethodBodyCoreAsync(bus, plan, ConditionKind, anchorTarget, block, insert, new
        {
            when,
            then,
            @else,
            insert = insert.ToString()
        });
    }

    public static Task<StepResponse> ApplyLoopAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string anchorTarget,
        string axis,
        string? collection,
        string item,
        string? pre,
        string? post,
        string? init,
        string? cond,
        string? step,
        string? body,
        BodyInsert insert,
        CancellationToken ct)
    {
        _ = ct;
        if (string.Equals(axis, "None", StringComparison.Ordinal))
            return Task.FromResult(StepResponse.Fail(LoopKind,
                "set one axis: OnCollection | PreCondition | PostCondition | WithCounter"));
        if (string.IsNullOrWhiteSpace(body))
            return Task.FromResult(StepResponse.Fail(LoopKind, "Body(…) is required"));

        string projected;
        try
        {
            projected = ProjectLoop(axis, collection, item, pre, post, init, cond, step, body!.Trim());
        }
        catch (Exception ex)
        {
            return Task.FromResult(StepResponse.Fail(LoopKind, ex.Message));
        }

        return InsertIntoMethodBodyCoreAsync(bus, plan, LoopKind, anchorTarget, projected, insert, new
        {
            axis,
            collection,
            item,
            pre,
            post,
            init,
            cond,
            step,
            insert = insert.ToString()
        });
    }

    private static string ProjectCondition(string when, string then, string? @else)
    {
        var thenBlock = AsBlock(then);
        if (string.IsNullOrWhiteSpace(@else))
            return $"if ({when})\n{thenBlock}\n";
        return $"if ({when})\n{thenBlock}\nelse\n{AsBlock(@else)}\n";
    }

    private static string ProjectLoop(
        string axis,
        string? collection,
        string item,
        string? pre,
        string? post,
        string? init,
        string? cond,
        string? step,
        string body)
    {
        var bodyBlock = AsBlock(body);
        return axis switch
        {
            "Collection" => string.IsNullOrWhiteSpace(collection)
                ? throw new ArgumentException("OnCollection requires collection expression")
                : $"foreach (var {item} in {collection.Trim()})\n{bodyBlock}\n",
            "Pre" => string.IsNullOrWhiteSpace(pre)
                ? throw new ArgumentException("PreCondition requires predicate")
                : $"while ({pre.Trim()})\n{bodyBlock}\n",
            "Post" => string.IsNullOrWhiteSpace(post)
                ? throw new ArgumentException("PostCondition requires predicate")
                : $"do\n{bodyBlock}\nwhile ({post.Trim()});\n",
            "Counter" when !string.IsNullOrWhiteSpace(init) && !string.IsNullOrWhiteSpace(cond)
                                                         && !string.IsNullOrWhiteSpace(step)
                => $"for ({TrimSemi(init!)}; {cond!.Trim()}; {TrimSemi(step!)})\n{bodyBlock}\n",
            "Counter" => throw new ArgumentException("WithCounter requires init, condition, step"),
            _ => throw new ArgumentException("unknown loop axis: " + axis)
        };
    }

    private static string AsBlock(string body)
    {
        var t = body.Trim();
        if (t.StartsWith('{'))
            return IndentBlock(t);
        var lines = t.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inner = string.Join("\n", lines.Select(l => "    " + l.TrimEnd()));
        return "{\n" + inner + "\n}";
    }

    private static string IndentBlock(string block)
    {
        var lines = block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join("\n", lines.Select(l => l));
    }

    private static string TrimSemi(string s)
    {
        var t = s.Trim();
        return t.EndsWith(';') ? t[..^1].TrimEnd() : t;
    }

    private static Task<StepResponse> InsertIntoMethodBodyCoreAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string kind,
        string anchorTarget,
        string snippet,
        BodyInsert insert,
        object meta)
    {
        if (!AnchorLocus.TryResolveFile(plan, anchorTarget, kind, out var file, out var span, out var fail))
            return Task.FromResult(fail!);

        if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(StepResponse.Fail(kind, "csharp projection only (.cs) for Body.* v1", new { file }));

        if (!BracketSyntaxResolve.TryFindAttachTarget(file, span, out var target, out var detail))
            return Task.FromResult(StepResponse.Fail(kind, $"locate failed: {detail}", new { anchor = anchorTarget }));

        var method = FindMethod(target.Node);
        if (method is null)
            return Task.FromResult(StepResponse.Fail(kind, "anchor must resolve to a method body (M:Method)", new
            {
                node = target.Node.Kind().ToString(),
                locate = detail
            }));

        var text = File.ReadAllText(file);
        if (method.Body is null)
        {
            if (method.ExpressionBody is null)
                return Task.FromResult(StepResponse.Fail(kind, "method has no body", new { method = method.Identifier.Text }));

            var expanded = ExpandExpressionBody(method);
            var newRoot = target.Root.ReplaceNode(method, expanded);
            text = newRoot.GetText().ToString();
            if (bus.IsDryRun)
            {
                var previewIndent = "    ";
                var dry = StepResponse.Success(kind, "dry_run", new
                {
                    dry_run = true,
                    path = file,
                    expanded_expression_body = true,
                    insert = insert.ToString(),
                    preview = IndentSnippet(snippet, previewIndent)
                });
                bus.RecordLocal("body", kind, ScriptArgs.From(new { kind, file, anchor = anchorTarget }), dry.ToJson(),
                    skippedDryRun: true);
                return Task.FromResult(dry);
            }

            File.WriteAllText(file, text);
            if (!BracketSyntaxResolve.TryFindAttachTarget(file, span, out target, out detail)
                || FindMethod(target.Node) is not { Body: not null } remethod)
            {
                return Task.FromResult(StepResponse.Fail(kind, "expand expression-body failed",
                    new { method = method.Identifier.Text }));
            }

            method = remethod;
            text = File.ReadAllText(file);
        }

        var body = method.Body!;
        var indent = GuessIndent(body);
        var indented = IndentSnippet(snippet, indent);
        var insertPos = insert == BodyInsert.AtStart
            ? body.OpenBraceToken.Span.End
            : body.CloseBraceToken.Span.Start;
        var before = text[..insertPos];
        var gap = before.EndsWith('\n') || before.EndsWith('\r') ? "" : "\n";
        var newText = before + gap + indented + "\n" + text[insertPos..];

        var args = ScriptArgs.From(new { kind, file, anchor = anchorTarget, method = method.Identifier.Text, meta });
        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(kind, "dry_run", new
            {
                dry_run = true,
                path = file,
                insert = insert.ToString(),
                preview = indented
            });
            bus.RecordLocal("body", kind, args, dry.ToJson(), skippedDryRun: true);
            return Task.FromResult(dry);
        }

        File.WriteAllText(file, newText);
        var result = StepResponse.Success(kind, "inserted:" + method.Identifier.Text, new
        {
            path = file,
            method = method.Identifier.Text,
            insert = insert.ToString(),
            locate = detail,
            work_root = plan.WorkRoot
        });
        bus.RecordLocal("body", kind, args, result.ToJson());
        return Task.FromResult(result);
    }

    private static MethodDeclarationSyntax ExpandExpressionBody(MethodDeclarationSyntax method)
    {
        var expr = method.ExpressionBody!.Expression;
        var isVoid = method.ReturnType is PredefinedTypeSyntax pt
                     && pt.Keyword.IsKind(SyntaxKind.VoidKeyword);
        StatementSyntax stmt = isVoid
            ? SyntaxFactory.ExpressionStatement(expr)
            : SyntaxFactory.ReturnStatement(expr);
        return method
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(SyntaxFactory.Block(stmt));
    }

    private static MethodDeclarationSyntax? FindMethod(SyntaxNode node)
    {
        for (var n = node; n is not null; n = n.Parent)
        {
            if (n is MethodDeclarationSyntax m)
                return m;
        }

        return null;
    }

    private static string GuessIndent(BlockSyntax body)
    {
        var openLine = body.OpenBraceToken.GetLocation().GetLineSpan().StartLinePosition.Line;
        var text = body.SyntaxTree.GetText();
        var line = text.Lines[openLine];
        var s = text.ToString(line.Span);
        var lead = 0;
        while (lead < s.Length && (s[lead] == ' ' || s[lead] == '\t'))
            lead++;
        var pad = s[..lead];
        return pad + (pad.Contains('\t', StringComparison.Ordinal) ? "\t" : "    ");
    }

    private static string IndentSnippet(string snippet, string indent)
    {
        var lines = snippet.Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd()
            .Split('\n');
        return string.Join("\n", lines.Select(l => string.IsNullOrWhiteSpace(l) ? l : indent + l));
    }
}
