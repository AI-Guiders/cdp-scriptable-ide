namespace Cdp.ScriptableIde;

/// <summary>Closed predicate intent for When / loop guards — operators projected; operands prefer <see cref="ExprIntent"/>.</summary>
public abstract record PredicateIntent;

public sealed record ComparePredicate(string Op, ExprIntent Left, ExprIntent Right) : PredicateIntent;

public sealed record NotPredicate(PredicateIntent Inner) : PredicateIntent;

public sealed record AndPredicate(PredicateIntent Left, PredicateIntent Right) : PredicateIntent;

public sealed record OrPredicate(PredicateIntent Left, PredicateIntent Right) : PredicateIntent;

public sealed record TruthPredicate(bool Value) : PredicateIntent;

/// <summary>Wire escape — prefer <see cref="Predicate.Lt"/> / <see cref="Predicate.And"/>.</summary>
public sealed record ExprPredicate(string Wire) : PredicateIntent;

/// <summary>Predicate nucleus (lang projection owns <c>&lt;</c> / <c>and</c> / <c>!</c>).</summary>
public static class Predicate
{
    public static PredicateIntent True { get; } = new TruthPredicate(true);
    public static PredicateIntent False { get; } = new TruthPredicate(false);

    public static PredicateIntent Lt(ExprIntent left, ExprIntent right) => Compare("lt", left, right);
    public static PredicateIntent Le(ExprIntent left, ExprIntent right) => Compare("le", left, right);
    public static PredicateIntent Gt(ExprIntent left, ExprIntent right) => Compare("gt", left, right);
    public static PredicateIntent Ge(ExprIntent left, ExprIntent right) => Compare("ge", left, right);
    public static PredicateIntent Eq(ExprIntent left, ExprIntent right) => Compare("eq", left, right);
    public static PredicateIntent Ne(ExprIntent left, ExprIntent right) => Compare("ne", left, right);

    /// <summary>Escape — string operands as expr wire.</summary>
    public static PredicateIntent Lt(string leftExpr, string rightExpr) =>
        Lt(global::Cdp.ScriptableIde.Expr.Wire(leftExpr), global::Cdp.ScriptableIde.Expr.Wire(rightExpr));

    public static PredicateIntent Le(string leftExpr, string rightExpr) =>
        Le(global::Cdp.ScriptableIde.Expr.Wire(leftExpr), global::Cdp.ScriptableIde.Expr.Wire(rightExpr));

    public static PredicateIntent Gt(string leftExpr, string rightExpr) =>
        Gt(global::Cdp.ScriptableIde.Expr.Wire(leftExpr), global::Cdp.ScriptableIde.Expr.Wire(rightExpr));

    public static PredicateIntent Ge(string leftExpr, string rightExpr) =>
        Ge(global::Cdp.ScriptableIde.Expr.Wire(leftExpr), global::Cdp.ScriptableIde.Expr.Wire(rightExpr));

    public static PredicateIntent Eq(string leftExpr, string rightExpr) =>
        Eq(global::Cdp.ScriptableIde.Expr.Wire(leftExpr), global::Cdp.ScriptableIde.Expr.Wire(rightExpr));

    public static PredicateIntent Ne(string leftExpr, string rightExpr) =>
        Ne(global::Cdp.ScriptableIde.Expr.Wire(leftExpr), global::Cdp.ScriptableIde.Expr.Wire(rightExpr));

    public static PredicateIntent Not(PredicateIntent inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new NotPredicate(inner);
    }

    public static PredicateIntent And(PredicateIntent left, PredicateIntent right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new AndPredicate(left, right);
    }

    public static PredicateIntent Or(PredicateIntent left, PredicateIntent right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new OrPredicate(left, right);
    }

    /// <summary>Rare escape — full predicate wire.</summary>
    public static PredicateIntent Wire(string wire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wire);
        return new ExprPredicate(wire.Trim());
    }

    /// <summary>Obsolete name — use <see cref="Wire"/>.</summary>
    public static PredicateIntent Expr(string wire) => Wire(wire);

    private static PredicateIntent Compare(string op, ExprIntent left, ExprIntent right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new ComparePredicate(op, left, right);
    }
}

public static class PredicateProjection
{
    public static bool TryProject(string language, PredicateIntent intent, out string wire, out string? error)
    {
        wire = "";
        error = null;
        try
        {
            wire = Project(language, intent);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string Project(string language, PredicateIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        language = (language ?? "csharp").Trim().ToLowerInvariant();
        return intent switch
        {
            TruthPredicate t => t.Value ? "true" : "false",
            ExprPredicate e => e.Wire,
            ComparePredicate c => ProjectCompare(language, c),
            NotPredicate n => ProjectNot(language, n.Inner),
            AndPredicate a => ProjectBinary(language, a.Left, a.Right, and: true),
            OrPredicate o => ProjectBinary(language, o.Left, o.Right, and: false),
            _ => throw new InvalidOperationException("unsupported PredicateIntent " + intent.GetType().Name)
        };
    }

    private static string ProjectCompare(string language, ComparePredicate c)
    {
        var op = c.Op switch
        {
            "lt" => "<",
            "le" => "<=",
            "gt" => ">",
            "ge" => ">=",
            "eq" => "==",
            "ne" => "!=",
            _ => throw new InvalidOperationException("unknown compare op " + c.Op)
        };
        var left = ExprProjection.Project(language, c.Left);
        var right = ExprProjection.Project(language, c.Right);
        return $"{left} {op} {right}";
    }

    private static string ProjectNot(string language, PredicateIntent inner)
    {
        var w = Project(language, inner);
        var needsParen = inner is AndPredicate or OrPredicate or ComparePredicate;
        var core = needsParen || inner is NotPredicate ? $"({w})" : w;
        return language == "python" ? $"not {core}" : $"!{core}";
    }

    private static string ProjectBinary(string language, PredicateIntent left, PredicateIntent right, bool and)
    {
        var l = ParenIfNeeded(language, left);
        var r = ParenIfNeeded(language, right);
        if (language == "python")
            return and ? $"{l} and {r}" : $"{l} or {r}";
        return and ? $"{l} && {r}" : $"{l} || {r}";
    }

    private static string ParenIfNeeded(string language, PredicateIntent p)
    {
        var w = Project(language, p);
        return p is AndPredicate or OrPredicate ? $"({w})" : w;
    }
}
