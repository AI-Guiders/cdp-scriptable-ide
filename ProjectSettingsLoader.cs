using System.Text.Json;
using Tomlyn;

namespace Cdp.ScriptableIde;

/// <summary>Load/save <see cref="ProjectSettings"/>; language-dependent Detect fills gaps.</summary>
public static class ProjectSettingsLoader
{
    private static readonly TomlSerializerOptions TomlOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>Read file (if any) then Detect-fill unset test FW.</summary>
    public static void Hydrate(PlanContext plan)
    {
        plan.Settings.SettingsPath = ProjectSettingsPaths.ResolveFile(plan.WorkRoot);
        TryReadFile(plan.Settings);
        FillDetect(plan);
    }

    public static void FillDetect(PlanContext plan)
    {
        if (plan.Settings.TestFramework is not null)
            return;
        if (plan.Settings.TestFrameworkPolicy is TestFrameworkPolicy.Specified)
            return;

        try
        {
            var r = TestFrameworkResolver.Resolve(plan, plan.Settings.TestFrameworkPolicy, specified: null);
            plan.Settings.TestFramework = r.Kind;
            plan.Settings.TestFrameworkSource = r.Source;
        }
        catch
        {
            var fallback = TestFrameworkResolver.DefaultForLanguage(plan.Language ?? "csharp");
            plan.Settings.TestFramework = fallback;
            plan.Settings.TestFrameworkSource = "fallback";
        }
    }

    public static void TryReadFile(ProjectSettings settings)
    {
        var path = settings.SettingsPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            var doc = TomlSerializer.Deserialize<ProjectTomlDocument>(File.ReadAllText(path), TomlOpts);
            if (doc?.Test is { } test)
            {
                if (!string.IsNullOrWhiteSpace(test.Policy)
                    && Enum.TryParse<TestFrameworkPolicy>(test.Policy, ignoreCase: true, out var policy))
                    settings.TestFrameworkPolicy = policy;

                if (!string.IsNullOrWhiteSpace(test.Framework)
                    && TryParseFramework(test.Framework, out var kind))
                {
                    settings.TestFramework = kind;
                    settings.TestFrameworkPolicy = TestFrameworkPolicy.Specified;
                    settings.TestFrameworkSource = "file";
                }
            }

            if (!string.IsNullOrWhiteSpace(doc?.Docs?.Style))
                settings.DocstringStyle = doc.Docs.Style;

            if (!string.IsNullOrWhiteSpace(doc?.Format?.Profile))
                settings.FormatProfile = doc.Format.Profile;
        }
        catch
        {
            // corrupt file — leave defaults
        }
    }

    public static void Save(PlanContext plan)
    {
        var path = string.IsNullOrWhiteSpace(plan.Settings.SettingsPath)
            ? ProjectSettingsPaths.ResolveFile(plan.WorkRoot)
            : plan.Settings.SettingsPath;
        plan.Settings.SettingsPath = path;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var fw = plan.Settings.TestFramework?.ToString().ToLowerInvariant() ?? "";
        var policy = plan.Settings.TestFrameworkPolicy.ToString();
        var doc = plan.Settings.DocstringStyle ?? "";
        var profile = plan.Settings.FormatProfile ?? "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# CDP project settings (harness-owned conventions)");
        sb.AppendLine();
        sb.AppendLine("[test]");
        if (plan.Settings.TestFramework is not null
            && plan.Settings.TestFrameworkPolicy == TestFrameworkPolicy.Specified)
            sb.AppendLine($"framework = \"{TomlEscape(fw)}\"");
        sb.AppendLine($"policy = \"{TomlEscape(policy)}\"");
        sb.AppendLine();
        sb.AppendLine("[docs]");
        if (!string.IsNullOrWhiteSpace(doc))
            sb.AppendLine($"style = \"{TomlEscape(doc)}\"");
        else
            sb.AppendLine("# style = \"google\"  # python docstring etc.");
        sb.AppendLine();
        sb.AppendLine("[format]");
        if (!string.IsNullOrWhiteSpace(profile))
            sb.AppendLine($"profile = \"{TomlEscape(profile)}\"");
        else
            sb.AppendLine("# profile = \"default\"");

        File.WriteAllText(path, sb.ToString());
        if (plan.Settings.TestFrameworkPolicy == TestFrameworkPolicy.Specified)
            plan.Settings.TestFrameworkSource = "file";
    }

    public static bool TryParseFramework(string text, out TestFrameworkKind kind)
    {
        var raw = text.Trim();
        if (Enum.TryParse(raw, ignoreCase: true, out kind))
            return true;

        var t = raw.ToLowerInvariant().Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        switch (t)
        {
            case "xunit":
                kind = TestFrameworkKind.Xunit;
                return true;
            case "nunit":
                kind = TestFrameworkKind.NUnit;
                return true;
            case "mstest":
                kind = TestFrameworkKind.MSTest;
                return true;
            case "jest":
                kind = TestFrameworkKind.Jest;
                return true;
            case "vitest":
                kind = TestFrameworkKind.Vitest;
                return true;
            case "nodetest":
            case "node:test":
            case "node":
                kind = TestFrameworkKind.NodeTest;
                return true;
            case "pytest":
                kind = TestFrameworkKind.Pytest;
                return true;
            case "unittest":
                kind = TestFrameworkKind.Unittest;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static string TomlEscape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed class ProjectTomlDocument
    {
        public ProjectTomlTest? Test { get; set; }
        public ProjectTomlDocs? Docs { get; set; }
        public ProjectTomlFormat? Format { get; set; }
    }

    private sealed class ProjectTomlTest
    {
        public string? Framework { get; set; }
        public string? Policy { get; set; }
    }

    private sealed class ProjectTomlDocs
    {
        public string? Style { get; set; }
    }

    private sealed class ProjectTomlFormat
    {
        public string? Profile { get; set; }
    }
}
