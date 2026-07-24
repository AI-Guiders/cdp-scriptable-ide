using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace Cdp.ScriptableIde;

/// <summary>
/// Live CSX surface help from assembly XML docs (not a static man page).
/// Prefer <c>Help.Toc()</c> / <c>Help.Of("Symbol")</c> before inventing facade APIs.
/// </summary>
public static class CsxHelpCatalog
{
    public const string SchemaVersion = "csx_help/v0";

    private static readonly Lazy<(XDocument? Doc, string? XmlPath, string? Note)> Docs = new(LoadDocs);

    /// <summary>Top-level ScriptGlobals facades with summaries.</summary>
    public static string Toc(int maxFacades = 48)
    {
        maxFacades = Math.Clamp(maxFacades, 1, 200);
        var (doc, xmlPath, note) = Docs.Value;
        var facades = typeof(ScriptGlobals)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.PropertyType.IsClass && p.PropertyType.Namespace == typeof(ScriptGlobals).Namespace)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Take(maxFacades)
            .Select(p =>
            {
                var typeId = "T:" + p.PropertyType.FullName;
                var propId = "P:" + typeof(ScriptGlobals).FullName + "." + p.Name;
                return new
                {
                    name = p.Name,
                    type = p.PropertyType.Name,
                    summary = FirstNonEmpty(SummaryOf(doc, propId), SummaryOf(doc, typeId)),
                    hint = $"Help.Of(\"{p.Name}\")"
                };
            })
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            schema = SchemaVersion,
            op = "toc",
            ok = true,
            xml_path = xmlPath,
            note,
            cue = "Use Help.Of(\"Symbol\") or Help.Of(\"SemanticMap.Explore\") — do not invent SearchAsync/Report.",
            facade_count = facades.Length,
            facades
        });
    }

    /// <summary>
    /// Members for a facade path: <c>Symbol</c>, <c>SemanticMap</c>, <c>Symbol.Named</c>.
    /// </summary>
    public static string Of(string path, int maxMembers = 40)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        maxMembers = Math.Clamp(maxMembers, 1, 200);
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new ArgumentException("path required, e.g. Symbol or SemanticMap.Explore");

        var (doc, xmlPath, note) = Docs.Value;
        var rootProp = typeof(ScriptGlobals).GetProperty(parts[0], BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
            ?? throw new ArgumentException($"Unknown facade '{parts[0]}'. Call Help.Toc() first.");

        var type = rootProp.PropertyType;
        string? focusMember = null;
        for (var i = 1; i < parts.Length; i++)
        {
            focusMember = parts[i];
            var nested = type.GetProperty(parts[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.PropertyType
                ?? type.GetNestedType(parts[i], BindingFlags.Public | BindingFlags.IgnoreCase);
            if (nested is not null)
            {
                type = nested;
                focusMember = null;
            }
        }

        var members = EnumerateMembers(type)
            .Where(m => focusMember is null || m.Name.Equals(focusMember, StringComparison.OrdinalIgnoreCase))
            .Take(maxMembers)
            .Select(m => new
            {
                kind = m.Kind,
                name = m.Name,
                signature = m.Signature,
                summary = SummaryOf(doc, m.DocId),
                example = ExampleCue(parts[0], m.Name, m.Kind)
            })
            .ToArray();

        if (members.Length == 0)
            throw new ArgumentException($"No members matched '{path}' on {type.Name}.");

        return JsonSerializer.Serialize(new
        {
            schema = SchemaVersion,
            op = "of",
            ok = true,
            path = string.Join('.', parts),
            type = type.Name,
            xml_path = xmlPath,
            note,
            member_count = members.Length,
            members
        });
    }

    private static IEnumerable<(string Kind, string Name, string Signature, string DocId)> EnumerateMembers(System.Type type)
    {
        foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(p => p.GetIndexParameters().Length == 0)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            yield return ("property", p.Name, $"{p.PropertyType.Name} {p.Name}", "P:" + type.FullName + "." + p.Name);
        }

        foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                     .Where(m => !m.IsSpecialName)
                     .OrderBy(m => m.Name, StringComparer.Ordinal)
                     .ThenBy(m => m.GetParameters().Length))
        {
            var parms = m.GetParameters();
            var sig = $"{FormatType(m.ReturnType)} {m.Name}({string.Join(", ", parms.Select(p => FormatType(p.ParameterType) + " " + p.Name))})";
            var docId = "M:" + type.FullName + "." + m.Name + FormatDocParams(parms);
            yield return ("method", m.Name, sig, docId);
        }
    }

    private static string FormatType(System.Type t)
    {
        if (t == typeof(void)) return "void";
        if (t == typeof(string)) return "string";
        if (t == typeof(Task)) return "Task";
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
            return "Task<" + FormatType(t.GetGenericArguments()[0]) + ">";
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
            return FormatType(t.GetGenericArguments()[0]) + "?";
        return t.Name;
    }

    private static string FormatDocParams(ParameterInfo[] parms)
    {
        if (parms.Length == 0) return "";
        return "(" + string.Join(",", parms.Select(DocTypeName)) + ")";
    }

    private static string DocTypeName(ParameterInfo p)
    {
        var t = p.ParameterType;
        if (t.IsByRef) t = t.GetElementType()!;
        if (t == typeof(string)) return "System.String";
        if (t == typeof(int)) return "System.Int32";
        if (t == typeof(bool)) return "System.Boolean";
        if (t == typeof(CancellationToken)) return "System.Threading.CancellationToken";
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
            return "System.Nullable{" + DocTypeNameCore(t.GetGenericArguments()[0]) + "}";
        return DocTypeNameCore(t);
    }

    private static string DocTypeNameCore(System.Type t) =>
        t.FullName?.Replace('+', '.') ?? t.Name;

    private static string? ExampleCue(string facade, string member, string kind)
    {
        if (facade.Equals("Symbol", StringComparison.OrdinalIgnoreCase) && member.Equals("Named", StringComparison.OrdinalIgnoreCase))
            return "Symbol.Named(\"TypeName\").In(\"File.cs\") then FindUsagesAsync / SemanticMap.Explore(...)";
        if (facade.Equals("SemanticMap", StringComparison.OrdinalIgnoreCase) && member.Equals("Explore", StringComparison.OrdinalIgnoreCase))
            return "await SemanticMap.Explore(Symbol.Named(\"X\").In(\"X.cs\")).Mode(\"related\").GetSceneAsync()";
        if (facade.Equals("Help", StringComparison.OrdinalIgnoreCase))
            return kind == "method" ? $"Help.{member}(...)" : null;
        return null;
    }

    private static string? SummaryOf(XDocument? doc, string memberId)
    {
        if (doc is null) return null;
        var el = doc.Descendants("member")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), memberId, StringComparison.Ordinal));
        var summary = el?.Element("summary")?.Value;
        if (string.IsNullOrWhiteSpace(summary)) return null;
        return CollapseWs(summary);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string CollapseWs(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static (XDocument? Doc, string? XmlPath, string? Note) LoadDocs()
    {
        var asm = typeof(ScriptGlobals).Assembly;
        var candidates = new[]
        {
            Path.ChangeExtension(asm.Location, ".xml"),
            Path.Combine(AppContext.BaseDirectory, asm.GetName().Name + ".xml")
        };
        foreach (var path in candidates.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                return (XDocument.Load(path), path, null);
            }
            catch (Exception ex)
            {
                return (null, path, "xml_load_failed:" + ex.GetType().Name);
            }
        }

        return (null, null, "xml_docs_missing — rebuild Cdp.ScriptableIde with GenerateDocumentationFile; Help still lists members via reflection.");
    }
}
