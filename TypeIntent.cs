namespace Cdp.ScriptableIde;

/// <summary>Closed type intent — harness projects wire; prefer catalog over <see cref="NamedTypeIntent"/>.</summary>
public abstract record TypeIntent;

public sealed record InferTypeIntent : TypeIntent
{
    public static InferTypeIntent Instance { get; } = new();
}

public sealed record PrimitiveTypeIntent(string Id) : TypeIntent;

public sealed record ListOfTypeIntent(TypeIntent Element) : TypeIntent;

public sealed record NullableTypeIntent(TypeIntent Inner) : TypeIntent;

/// <summary>Simple type id (e.g. Counter) — identifier / dotted; no angle-brackets.</summary>
public sealed record OfTypeIntent(string Identifier) : TypeIntent;

/// <summary>Wire escape — syntax tax. Prefer <see cref="Type.ListOf"/> / <see cref="Type.Of"/>.</summary>
public sealed record NamedTypeIntent(string Wire) : TypeIntent;

/// <summary>Shared type nucleus for declare intents (lang projection owns syntax).</summary>
public static class Type
{
    public static TypeIntent Infer => InferTypeIntent.Instance;

    public static TypeIntent Integer => new PrimitiveTypeIntent("integer");
    public static TypeIntent Boolean => new PrimitiveTypeIntent("boolean");
    public static TypeIntent String => new PrimitiveTypeIntent("string");
    public static TypeIntent Float => new PrimitiveTypeIntent("float");
    public static TypeIntent Double => new PrimitiveTypeIntent("double");
    public static TypeIntent Object => new PrimitiveTypeIntent("object");

    public static TypeIntent ListOf(TypeIntent element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return new ListOfTypeIntent(element);
    }

    public static TypeIntent Nullable(TypeIntent inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new NullableTypeIntent(inner);
    }

    public static TypeIntent Of(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return new OfTypeIntent(identifier.Trim());
    }

    /// <summary>Rare escape — agent-authored wire (e.g. exotic generics). Prefer catalog ctors.</summary>
    public static TypeIntent Named(string wire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wire);
        return new NamedTypeIntent(wire.Trim());
    }
}

/// <summary>Project <see cref="TypeIntent"/> → language type wire (not a full type system).</summary>
public static class TypeProjection
{
    public static bool TryProject(string language, TypeIntent intent, out string typeWire, out string? error)
    {
        typeWire = "";
        error = null;
        language = (language ?? "csharp").Trim().ToLowerInvariant();
        try
        {
            typeWire = Project(language, intent);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string Project(string language, TypeIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        language = (language ?? "csharp").Trim().ToLowerInvariant();
        return intent switch
        {
            InferTypeIntent => throw new InvalidOperationException(
                "Type.Infer has no type wire — use var/omit at declare site"),
            PrimitiveTypeIntent p => ProjectPrimitive(language, p.Id),
            ListOfTypeIntent list => ProjectList(language, list.Element),
            NullableTypeIntent n => ProjectNullable(language, n.Inner),
            OfTypeIntent of => ProjectOf(of.Identifier),
            NamedTypeIntent named => named.Wire,
            _ => throw new InvalidOperationException("unsupported TypeIntent " + intent.GetType().Name)
        };
    }

    private static string ProjectPrimitive(string language, string id) => language switch
    {
        "python" => id switch
        {
            "integer" => "int",
            "boolean" => "bool",
            "string" => "str",
            "float" or "double" => "float",
            "object" => "object",
            _ => throw new InvalidOperationException("unknown primitive " + id)
        },
        "typescript" => id switch
        {
            "integer" or "float" or "double" => "number",
            "boolean" => "boolean",
            "string" => "string",
            "object" => "object",
            _ => throw new InvalidOperationException("unknown primitive " + id)
        },
        _ => id switch
        {
            "integer" => "int",
            "boolean" => "bool",
            "string" => "string",
            "float" => "float",
            "double" => "double",
            "object" => "object",
            _ => throw new InvalidOperationException("unknown primitive " + id)
        }
    };

    private static string ProjectList(string language, TypeIntent element)
    {
        var inner = Project(language, element);
        return language switch
        {
            "python" => $"list[{inner}]",
            "typescript" => $"{inner}[]",
            _ => $"List<{inner}>"
        };
    }

    private static string ProjectNullable(string language, TypeIntent inner)
    {
        var t = Project(language, inner);
        return language switch
        {
            "python" => $"{t} | None",
            "typescript" => $"{t} | null",
            _ => $"{t}?"
        };
    }

    private static string ProjectOf(string identifier)
    {
        if (!IsTypeName(identifier))
            throw new InvalidOperationException(
                "Type.Of requires identifier or dotted name (no < >); use Type.ListOf / Type.Named for the rest");
        return identifier;
    }

    internal static bool IsTypeName(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return false;
        // Counter or A.B.Counter — no generics / arrays in Of
        if (s.Contains('<', StringComparison.Ordinal)
            || s.Contains('>', StringComparison.Ordinal)
            || s.Contains('[', StringComparison.Ordinal)
            || s.Contains(']', StringComparison.Ordinal)
            || s.Contains('?', StringComparison.Ordinal)
            || s.Contains(',', StringComparison.Ordinal))
            return false;
        var parts = s.Split('.');
        foreach (var p in parts)
        {
            if (p.Length == 0)
                return false;
            if (!(char.IsLetter(p[0]) || p[0] == '_'))
                return false;
            for (var i = 1; i < p.Length; i++)
            {
                if (!(char.IsLetterOrDigit(p[i]) || p[i] == '_'))
                    return false;
            }
        }

        return true;
    }
}

/// <summary>Alias used by Convert/Create facades.</summary>
public static class Types
{
    public static TypeIntent Of(string identifier) => Type.Of(identifier);
    public static TypeIntent Infer => Type.Infer;
    public static TypeIntent Named(string wire) => Type.Named(wire);
}

