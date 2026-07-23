namespace Cdp.ScriptableIde;

/// <summary>Closed statement intent for Then / loop Body — no semicolon in agent surface.</summary>
public abstract record StmtIntent;

public sealed record ReturnExprStmt(string ExprWire) : StmtIntent;

public sealed record ReturnExprIntentStmt(ExprIntent Expr) : StmtIntent;

public sealed record ReturnLitStmt(string Text) : StmtIntent;

/// <summary>C# <c>$"…"</c> / later lang interp — template like <c>one:{x}</c> (no outer quotes).</summary>
public sealed record ReturnInterpStmt(string Template) : StmtIntent;

public sealed record DeclareStmt(DeclareArrange Arrange) : StmtIntent;

/// <summary>Wire escape — prefer catalog. Trailing <c>;</c> stripped if present.</summary>
public sealed record WireStmt(string Wire) : StmtIntent;

public static class Stmt
{
    /// <summary>Return expression — agent omits <c>;</c> (e.g. <c>x</c>, <c>a + b</c>).</summary>
    public static StmtIntent Return(string exprWire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exprWire);
        return new ReturnExprStmt(StripSemi(exprWire.Trim()));
    }

    /// <summary>Return via closed <see cref="ExprIntent"/>.</summary>
    public static StmtIntent Return(ExprIntent expr)
    {
        ArgumentNullException.ThrowIfNull(expr);
        return new ReturnExprIntentStmt(expr);
    }

    /// <summary>Return string literal — no quotes/escapes in agent call: <c>ReturnLit("no real roots")</c>.</summary>
    public static StmtIntent ReturnLit(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ReturnLitStmt(text);
    }

    /// <summary>Return interpolated string — template <c>one:{x}</c> → csharp <c>$"one:{x}"</c>.</summary>
    public static StmtIntent ReturnInterp(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        return new ReturnInterpStmt(template.Trim());
    }

    public static StmtIntent Declare(DeclareBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return new DeclareStmt((DeclareArrange)builder.ToArrange());
    }

    public static StmtIntent Declare(DeclareArrange arrange)
    {
        ArgumentNullException.ThrowIfNull(arrange);
        return new DeclareStmt(arrange);
    }

    /// <summary>Rare escape — full statement wire; <c>;</c> optional.</summary>
    public static StmtIntent Wire(string statementWire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statementWire);
        return new WireStmt(statementWire.Trim());
    }

    private static string StripSemi(string s) =>
        s.EndsWith(';') ? s[..^1].TrimEnd() : s;
}

public static class StmtProjection
{
    public static bool TryProject(string language, StmtIntent intent, out string wire, out string? error)
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

    public static bool TryProjectBlock(string language, IReadOnlyList<StmtIntent> stmts, out string body, out string? error)
    {
        body = "";
        error = null;
        if (stmts.Count == 0)
        {
            error = "Then/Body requires at least one Stmt";
            return false;
        }

        var lines = new List<string>();
        foreach (var s in stmts)
        {
            if (!TryProject(language, s, out var line, out error))
                return false;
            lines.Add(line);
        }

        body = string.Join("\n", lines);
        return true;
    }

    public static string Project(string language, StmtIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        language = (language ?? "csharp").Trim().ToLowerInvariant();
        return intent switch
        {
            ReturnExprStmt r => ProjectReturnExpr(language, r.ExprWire),
            ReturnExprIntentStmt re => ProjectReturnExpr(language, ExprProjection.Project(language, re.Expr)),
            ReturnLitStmt lit => ProjectReturnLit(language, lit.Text),
            ReturnInterpStmt interp => ProjectReturnInterp(language, interp.Template),
            DeclareStmt d => DeclareProjection.Project(language, d.Arrange),
            WireStmt w => NormalizeWire(language, w.Wire),
            _ => throw new InvalidOperationException("unsupported StmtIntent " + intent.GetType().Name)
        };
    }

    private static string ProjectReturnExpr(string language, string expr) => language switch
    {
        "python" => $"return {expr}",
        _ => $"return {expr};"
    };

    private static string ProjectReturnLit(string language, string text)
    {
        var escaped = EscapeStringLiteral(language, text);
        return language switch
        {
            "python" => $"return {escaped}",
            _ => $"return {escaped};"
        };
    }

    private static string ProjectReturnInterp(string language, string template) => language switch
    {
        "python" => $"return f{EscapeStringLiteral("python", template)}",
        "typescript" => $"return `{template}`;", // holes {x} → need ${x} for ts — thin: leave {x} as csharp-ish; v1 csharp primary
        _ => $"return $\"{EscapeInterpInner(template)}\";"
    };

    private static string EscapeStringLiteral(string language, string text)
    {
        if (language == "python")
        {
            var e = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
            return "\"" + e + "\"";
        }

        var c = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return "\"" + c + "\"";
    }

    private static string EscapeInterpInner(string template) =>
        template.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string NormalizeWire(string language, string wire)
    {
        var t = wire.Trim();
        if (language == "python")
            return t.EndsWith(';') ? t[..^1].TrimEnd() : t;
        return t.EndsWith(';') ? t : t + ";";
    }
}
