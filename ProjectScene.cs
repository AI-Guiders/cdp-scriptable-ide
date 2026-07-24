namespace Cdp.ScriptableIde;

/// <summary>Compact project map before <c>Projects.Create</c> — mirrors <c>git_scene</c> (templates + session + existing).</summary>
public static class ProjectScene
{
    public const string SchemaVersion = "project_scene/v0";
    public const int MaxExistingDefault = 40;
    public const int MaxInstalledDefault = 80;

    public sealed record TemplateCard(
        string Id,
        string Title,
        string Language,
        string Tags,
        string CreateVia);

    /// <summary>VS-like shortlist — prefer these over inventing paths by hand.</summary>
    public static readonly TemplateCard[] Curated =
    [
        new("console", "Console App", "csharp", "Common/Console", "dotnet_new"),
        new("classlib", "Class Library", "csharp", "Common/Library", "dotnet_new"),
        new("xunit", "xUnit Test Project", "csharp", "Test/xUnit", "dotnet_new"),
        new("nunit", "NUnit Test Project", "csharp", "Test/NUnit", "dotnet_new"),
        new("mstest", "MSTest Test Project", "csharp", "Test/MSTest", "dotnet_new"),
        new("webapi", "ASP.NET Core Web API", "csharp", "Web/WebAPI", "dotnet_new"),
        new("worker", "Worker Service", "csharp", "Common/Worker", "dotnet_new"),
        new("mcp", "MCP server (full)", "csharp", "Common/Tool/MCP", "dotnet_new"),
        new("mcp-min", "MCP server (minimal)", "csharp", "Common/Tool/MCP", "dotnet_new"),
        new("avalonia.app", "Avalonia .NET App", "csharp", "Desktop/Avalonia", "dotnet_new"),
        new("typescript", "TypeScript package (npm init + tsconfig)", "typescript", "Node/Library", "npm_init")
    ];

    public static IReadOnlyList<object> PolicyEnums() =>
    [
        new { kind = "tfm_policy", values = new[] { "prefer_most_used", "latest", "lts", "specified" } },
        new { kind = "engine_policy", values = new[] { "prefer_most_used", "latest", "lts", "specified" } }
    ];

    /// <summary>Parse <c>dotnet new list --type project</c> table rows → short names.</summary>
    public static List<TemplateCard> ParseDotnetNewList(string stdout, int max)
    {
        var list = new List<TemplateCard>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith("Template Name", StringComparison.Ordinal)
                || line.StartsWith("----", StringComparison.Ordinal)
                || line.StartsWith("These templates", StringComparison.Ordinal))
                continue;

            // Name … ShortName(s) … Language … Tags — short name column is space-padded; take last token groups.
            // Practical: find first token that looks like a short-name (no spaces in middle of field).
            var parts = SplitColumns(line);
            if (parts.Count < 2)
                continue;
            var title = parts[0];
            var shortField = parts[1];
            var language = parts.Count > 2 ? parts[2] : "";
            var tags = parts.Count > 3 ? parts[3] : "";
            foreach (var id in shortField.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!seen.Add(id))
                    continue;
                list.Add(new TemplateCard(id, title, language, tags, "dotnet_new"));
                if (list.Count >= max)
                    return list;
            }
        }

        return list;
    }

    /// <summary>Heuristic column split for fixed-width <c>dotnet new list</c> output.</summary>
    public static List<string> SplitColumns(string line)
    {
        // Collapse 2+ spaces as column separators (dotnet list uses wide padding).
        var cols = new List<string>();
        var start = 0;
        while (start < line.Length)
        {
            while (start < line.Length && line[start] == ' ')
                start++;
            if (start >= line.Length)
                break;
            var i = start;
            while (i < line.Length)
            {
                if (line[i] == ' ' && i + 1 < line.Length && line[i + 1] == ' ')
                    break;
                i++;
            }

            cols.Add(line[start..i].Trim());
            start = i;
        }

        return cols;
    }
}
