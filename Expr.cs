using System.Globalization;

namespace Cdp.ScriptableIde;

/// <summary>Closed expression intent — harness projects wire; prefer catalog over <see cref="WireExpr"/>.</summary>
public abstract record ExprIntent;

public sealed record IdExpr(string Name) : ExprIntent;

public sealed record LitIntExpr(long Value) : ExprIntent;

public sealed record LitDoubleExpr(double Value) : ExprIntent;

public sealed record LitStringExpr(string Value) : ExprIntent;

public sealed record BinExpr(string Op, ExprIntent Left, ExprIntent Right) : ExprIntent;

public sealed record UnaryExpr(string Op, ExprIntent Inner) : ExprIntent;

public sealed record CallExpr(ExprIntent? Receiver, string Method, IReadOnlyList<ExprIntent> Args) : ExprIntent;

public sealed record GroupExpr(ExprIntent Inner) : ExprIntent;

/// <summary>Escape — raw expr wire. Prefer <see cref="Expr.Id"/> / <see cref="Expr.Add"/>.</summary>
public sealed record WireExpr(string Wire) : ExprIntent;

/// <summary>
/// Thin expr nucleus for Declare / Predicate / Stmt — not a full expression algebra
/// (no lambdas, casts, generics, indexing…). Escape: <see cref="Wire"/>.
/// </summary>
public static class Expr
{
    public static ExprIntent Id(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new IdExpr(name.Trim());
    }

    public static ExprIntent Lit(int value) => new LitIntExpr(value);
    public static ExprIntent Lit(long value) => new LitIntExpr(value);
    public static ExprIntent Lit(double value) => new LitDoubleExpr(value);

    public static ExprIntent Lit(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new LitStringExpr(text);
    }

    public static ExprIntent Add(ExprIntent left, ExprIntent right) => Bin("add", left, right);
    public static ExprIntent Sub(ExprIntent left, ExprIntent right) => Bin("sub", left, right);
    public static ExprIntent Mul(ExprIntent left, ExprIntent right) => Bin("mul", left, right);
    public static ExprIntent Div(ExprIntent left, ExprIntent right) => Bin("div", left, right);

    public static ExprIntent Neg(ExprIntent inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new UnaryExpr("neg", inner);
    }

    public static ExprIntent Group(ExprIntent inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new GroupExpr(inner);
    }

    public static ExprIntent Call(string method, params ExprIntent[] args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return new CallExpr(null, method.Trim(), args ?? []);
    }

    public static ExprIntent Call(ExprIntent receiver, string method, params ExprIntent[] args)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return new CallExpr(receiver, method.Trim(), args ?? []);
    }

    /// <summary>Rare escape — agent-authored expr wire.</summary>
    public static ExprIntent Wire(string exprWire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exprWire);
        return new WireExpr(exprWire.Trim());
    }

    private static ExprIntent Bin(string op, ExprIntent left, ExprIntent right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new BinExpr(op, left, right);
    }
}

/// <summary>Project <see cref="ExprIntent"/> → language expr wire (csharp-first; python/ts operators shared).</summary>
public static class ExprProjection
{
    public static bool TryProject(string language, ExprIntent intent, out string wire, out string? error)
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

    public static string Project(string language, ExprIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        language = (language ?? "csharp").Trim().ToLowerInvariant();
        return ProjectCore(language, intent);
    }

    private static string ProjectCore(string language, ExprIntent intent) => intent switch
    {
        IdExpr id => id.Name,
        LitIntExpr i => i.Value.ToString(CultureInfo.InvariantCulture),
        LitDoubleExpr d => FormatDouble(d.Value),
        LitStringExpr s => language == "python"
            ? ToPythonString(s.Value)
            : ToCSharpString(s.Value),
        WireExpr w => w.Wire,
        GroupExpr g => $"({ProjectCore(language, g.Inner)})",
        UnaryExpr u => ProjectUnary(language, u),
        BinExpr b => ProjectBin(language, b),
        CallExpr c => ProjectCall(language, c),
        _ => throw new InvalidOperationException("unsupported ExprIntent " + intent.GetType().Name)
    };

    private static string ProjectUnary(string language, UnaryExpr u)
    {
        _ = language;
        if (u.Op != "neg")
            throw new InvalidOperationException("unknown unary op " + u.Op);
        var inner = ProjectCore(language, u.Inner);
        return NeedsParenAsUnaryOperand(u.Inner) ? $"-({inner})" : $"-{inner}";
    }

    private static string ProjectBin(string language, BinExpr b)
    {
        var op = b.Op switch
        {
            "add" => "+",
            "sub" => "-",
            "mul" => "*",
            "div" => "/",
            _ => throw new InvalidOperationException("unknown bin op " + b.Op)
        };
        var prec = Prec(b.Op);
        var left = ProjectChild(language, b.Left, prec, isRight: false, parentOp: b.Op);
        var right = ProjectChild(language, b.Right, prec, isRight: true, parentOp: b.Op);
        return $"{left} {op} {right}";
    }

    private static string ProjectCall(string language, CallExpr c)
    {
        _ = language;
        var args = string.Join(", ", c.Args.Select(a => ProjectCore(language, a)));
        if (c.Receiver is null)
            return $"{c.Method}({args})";
        var recv = ProjectChild(language, c.Receiver, Prec("call"), isRight: false, parentOp: "call");
        return $"{recv}.{c.Method}({args})";
    }

    private static string ProjectChild(string language, ExprIntent child, int parentPrec, bool isRight, string parentOp)
    {
        var w = ProjectCore(language, child);
        if (child is WireExpr or GroupExpr or IdExpr or LitIntExpr or LitDoubleExpr or LitStringExpr or CallExpr)
            return w;
        if (child is UnaryExpr)
            return parentPrec > Prec("neg") ? w : w; // unary high enough
        if (child is BinExpr b)
        {
            var childPrec = Prec(b.Op);
            if (childPrec < parentPrec)
                return $"({w})";
            // left-assoc: paren right child when same prec for sub/div
            if (isRight && childPrec == parentPrec && parentOp is "sub" or "div")
                return $"({w})";
            return w;
        }

        return w;
    }

    private static bool NeedsParenAsUnaryOperand(ExprIntent inner) =>
        inner is BinExpr or WireExpr;

    private static int Prec(string op) => op switch
    {
        "add" or "sub" => 1,
        "mul" or "div" => 2,
        "neg" => 3,
        "call" => 4,
        _ => 0
    };

    private static string FormatDouble(double v)
    {
        if (double.IsInteger(v) && Math.Abs(v) < 1e15)
            return ((long)v).ToString(CultureInfo.InvariantCulture);
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string ToCSharpString(string s) =>
        "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string ToPythonString(string s) =>
        "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
