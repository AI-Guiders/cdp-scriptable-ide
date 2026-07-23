namespace Cdp.ScriptableIde;

/// <summary>Project <see cref="DeclareArrange"/> → statement wire (shared by TestMethod Arrange + Body.AddDeclare).</summary>
public static class DeclareProjection
{
    public static bool TryProject(string language, DeclareArrange d, out string line, out string? error)
    {
        line = "";
        error = null;
        if (!IsValidIdentifier(d.Local))
        {
            error = "Declare local must be an identifier";
            return false;
        }

        language = (language ?? "csharp").Trim().ToLowerInvariant();
        var infer = d.Type is InferTypeIntent;
        string? value = null;
        if (d.ValueExpr is { } expr)
        {
            if (!ExprProjection.TryProject(language, expr, out value, out error))
                return false;
        }
        else
        {
            value = d.ValueWire;
        }

        if (infer && string.IsNullOrWhiteSpace(value))
        {
            error = "Types.Infer requires Value";
            return false;
        }

        if (d.IsConstant && infer && language is "csharp" or "")
        {
            error = "Declare.Constant + Types.Infer unsupported in csharp (const needs explicit type)";
            return false;
        }

        string? typeWire = null;
        if (!infer)
        {
            if (!TypeProjection.TryProject(language, d.Type, out typeWire, out error))
                return false;
        }

        line = language switch
        {
            "python" => ProjectPython(d.Local, typeWire, value, d.IsConstant, infer),
            "typescript" => ProjectTypeScript(d.Local, typeWire, value, d.IsConstant, infer),
            _ => ProjectCSharp(d.Local, typeWire, value, d.IsConstant, infer)
        };
        return true;
    }

    public static string Project(string language, DeclareArrange d)
    {
        if (!TryProject(language, d, out var line, out var err))
            throw new InvalidOperationException(err ?? "declare project failed");
        return line;
    }

    private static string ProjectCSharp(
        string local, string? typeWire, string? value, bool isConstant, bool infer)
    {
        if (isConstant)
            return $"const {typeWire} {local} = {value};";
        if (infer)
            return $"var {local} = {value};";
        if (string.IsNullOrWhiteSpace(value))
            return $"{typeWire} {local};";
        return $"{typeWire} {local} = {value};";
    }

    private static string ProjectTypeScript(
        string local, string? typeWire, string? value, bool isConstant, bool infer)
    {
        var kw = isConstant ? "const" : "let";
        if (infer || typeWire is null)
        {
            if (string.IsNullOrWhiteSpace(value))
                return $"{kw} {local};";
            return $"{kw} {local} = {value};";
        }

        if (string.IsNullOrWhiteSpace(value))
            return $"{kw} {local}: {typeWire};";
        return $"{kw} {local}: {typeWire} = {value};";
    }

    private static string ProjectPython(
        string local, string? typeWire, string? value, bool isConstant, bool infer)
    {
        if (isConstant)
        {
            if (infer || typeWire is null)
                return string.IsNullOrWhiteSpace(value) ? $"{local}: Final" : $"{local}: Final = {value}";
            return string.IsNullOrWhiteSpace(value)
                ? $"{local}: Final[{typeWire}]"
                : $"{local}: Final[{typeWire}] = {value}";
        }

        if (infer || typeWire is null)
            return string.IsNullOrWhiteSpace(value) ? local : $"{local} = {value}";
        return string.IsNullOrWhiteSpace(value)
            ? $"{local}: {typeWire}"
            : $"{local}: {typeWire} = {value}";
    }

    private static bool IsValidIdentifier(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return false;
        if (!(char.IsLetter(s[0]) || s[0] == '_'))
            return false;
        for (var i = 1; i < s.Length; i++)
        {
            if (!(char.IsLetterOrDigit(s[i]) || s[i] == '_'))
                return false;
        }

        return true;
    }
}
