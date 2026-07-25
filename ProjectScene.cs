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

    /// <summary>
    /// Host / protected roots are unsafe for AllDirectories project scans
    /// (MCP cold cwd often lands under AppData → Access denied on Application Data).
    /// </summary>
    public static bool IsHostHabitatRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return true;
        }

        full = TrimDir(full);
        if (full.Length == 0)
            return true;

        // Classic junction that throws Access denied on AllDirectories walk.
        if (full.Contains("Application Data", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.System,
                     Environment.SpecialFolder.SystemX86,
                     Environment.SpecialFolder.CommonApplicationData
                 })
        {
            if (Under(Environment.GetFolderPath(special), full))
                return true;
        }

        // Exact AppData roots (not Temp / project folders nested under Local).
        if (EqualsDir(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), full)
            || EqualsDir(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), full))
            return true;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (EqualsDir(profile, full))
            return true;

        var driveRoot = Path.GetPathRoot(full);
        if (EqualsDir(driveRoot, full))
            return true;

        return false;
    }

    public const string HostHabitatScanNote =
        "host habitat cwd — pass root= or cdp_open (no deep existing scan)";

    static bool Under(string? root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;
        string r;
        try { r = TrimDir(Path.GetFullPath(root)); }
        catch { return false; }
        if (r.Length == 0) return false;
        if (EqualsDir(r, candidate)) return true;
        var sep = Path.DirectorySeparatorChar;
        var alt = Path.AltDirectorySeparatorChar;
        return candidate.StartsWith(r + sep, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(r + alt, StringComparison.OrdinalIgnoreCase);
    }

    static bool EqualsDir(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        return string.Equals(TrimDir(a), TrimDir(b), StringComparison.OrdinalIgnoreCase);
    }

    static string TrimDir(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

}
