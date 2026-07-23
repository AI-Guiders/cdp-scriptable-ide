namespace Cdp.ScriptableIde;

/// <summary>Closed access intent — language projects modifiers (csharp: public/private/…).</summary>
public enum AccessIntent
{
    Public = 0,
    Private = 1,
    Protected = 2,
    Internal = 3
}

public static class AccessProjection
{
    public static bool TryProject(string language, AccessIntent access, bool topLevelType, out string wire, out string? error)
    {
        wire = "";
        error = null;
        language = (language ?? "csharp").Trim().ToLowerInvariant();
        if (language is not "csharp")
        {
            error = "access projection csharp-only v1";
            return false;
        }

        if (topLevelType && access is AccessIntent.Private or AccessIntent.Protected)
        {
            error = "top-level type cannot be Private/Protected in csharp";
            return false;
        }

        wire = access switch
        {
            AccessIntent.Public => "public",
            AccessIntent.Private => "private",
            AccessIntent.Protected => "protected",
            AccessIntent.Internal => "internal",
            _ => "public"
        };
        return true;
    }
}

/// <summary>Field intent for Create composition: <c>Field.Named("x").Of(Types.String)</c>.</summary>
public static class Field
{
    public static FieldBuilder Named(string name) => new(name);
}

public sealed class FieldBuilder(string name)
{
    public FieldSpec Of(TypeIntent type)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new FieldSpec(name.Trim(), type);
    }
}

public sealed record FieldSpec(string Name, TypeIntent Type);

internal static class CsharpIdents
{
    public static bool IsIdent(string s)
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
