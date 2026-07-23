using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cdp.ScriptableIde;

/// <summary>How to pick a test framework — same grain as <see cref="TfmPolicy"/>.</summary>
public enum TestFrameworkPolicy
{
    /// <summary>Read project deps / existing test markers; fallback = language default.</summary>
    Detect,
    /// <summary>Mode across nearby projects (v1: same as Detect).</summary>
    PreferInRepo,
    /// <summary>Explicit <see cref="TestFrameworkKind"/> from authoring.</summary>
    Specified
}

/// <summary>Closed catalog of test FW projections (extend slowly).</summary>
public enum TestFrameworkKind
{
    // csharp
    Xunit,
    NUnit,
    MSTest,
    // typescript
    Jest,
    Vitest,
    NodeTest,
    // python
    Pytest,
    Unittest
}

public sealed record TestFrameworkResolution(
    TestFrameworkKind Kind,
    string Language,
    string Source,
    string? PackageId,
    bool PackagePresent);

public static partial class TestFrameworkResolver
{
    [GeneratedRegex(@"PackageReference\s+Include\s*=\s*""(?<id>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NugetPackageRef();

    /// <summary>
    /// Per-call override → project settings pin → Detect.
    /// </summary>
    public static TestFrameworkResolution ResolveEffective(
        PlanContext plan,
        TestFrameworkPolicy callPolicy,
        TestFrameworkKind? callSpecified)
    {
        if (callSpecified is not null)
            return Resolve(plan, TestFrameworkPolicy.Specified, callSpecified);

        if (callPolicy == TestFrameworkPolicy.Specified && plan.Settings.TestFramework is { } must)
            return Resolve(plan, TestFrameworkPolicy.Specified, must);

        if (IsSettingsPinned(plan.Settings) && plan.Settings.TestFramework is { } pinned)
            return FromSettings(plan, pinned);

        if (plan.Settings.TestFramework is { } cached
            && callPolicy is TestFrameworkPolicy.Detect or TestFrameworkPolicy.PreferInRepo)
            return FromSettings(plan, cached);

        return Resolve(plan, callPolicy, specified: null);
    }

    private static bool IsSettingsPinned(ProjectSettings s) =>
        s.TestFrameworkPolicy == TestFrameworkPolicy.Specified
        || string.Equals(s.TestFrameworkSource, "file", StringComparison.Ordinal)
        || string.Equals(s.TestFrameworkSource, "set", StringComparison.Ordinal)
        || string.Equals(s.TestFrameworkSource, "set+file", StringComparison.Ordinal);

    private static TestFrameworkResolution FromSettings(PlanContext plan, TestFrameworkKind kind)
    {
        var lang = LanguageFor(kind);
        var pkg = PackageIdFor(kind);
        return new TestFrameworkResolution(
            kind,
            lang,
            plan.Settings.TestFrameworkSource ?? "settings",
            pkg,
            IsPackagePresent(plan, lang, pkg));
    }

    public static TestFrameworkKind DefaultForLanguage(string language) =>
        NormalizeLang(language) switch
        {
            "typescript" => TestFrameworkKind.Vitest,
            "python" => TestFrameworkKind.Pytest,
            _ => TestFrameworkKind.Xunit
        };

    public static string? PackageIdFor(TestFrameworkKind kind) => kind switch
    {
        TestFrameworkKind.Xunit => "xunit",
        TestFrameworkKind.NUnit => "NUnit",
        TestFrameworkKind.MSTest => "MSTest.TestFramework",
        TestFrameworkKind.Jest => "jest",
        TestFrameworkKind.Vitest => "vitest",
        TestFrameworkKind.NodeTest => null,
        TestFrameworkKind.Pytest => "pytest",
        TestFrameworkKind.Unittest => null,
        _ => null
    };

    public static string LanguageFor(TestFrameworkKind kind) => kind switch
    {
        TestFrameworkKind.Xunit or TestFrameworkKind.NUnit or TestFrameworkKind.MSTest => "csharp",
        TestFrameworkKind.Jest or TestFrameworkKind.Vitest or TestFrameworkKind.NodeTest => "typescript",
        TestFrameworkKind.Pytest or TestFrameworkKind.Unittest => "python",
        _ => "csharp"
    };

    public static TestFrameworkResolution Resolve(
        PlanContext plan,
        TestFrameworkPolicy policy,
        TestFrameworkKind? specified = null)
    {
        var lang = NormalizeLang(plan.Language);
        if (policy == TestFrameworkPolicy.Specified)
        {
            if (specified is null)
                throw new ArgumentException("Specified policy requires TestFrameworkKind.");
            var pkg = PackageIdFor(specified.Value);
            var present = pkg is null || IsPackagePresent(plan, lang, pkg);
            return new TestFrameworkResolution(specified.Value, LanguageFor(specified.Value), "specified", pkg, present);
        }

        // Detect / PreferInRepo
        if (TryDetect(plan, lang, out var detected, out var src, out var pkgId, out var presentDet))
            return new TestFrameworkResolution(detected, lang, src, pkgId, presentDet);

        var fallback = DefaultForLanguage(lang);
        return new TestFrameworkResolution(
            fallback,
            lang,
            "fallback",
            PackageIdFor(fallback),
            IsPackagePresent(plan, lang, PackageIdFor(fallback)));
    }

    public static bool TryDetect(
        PlanContext plan,
        string language,
        out TestFrameworkKind kind,
        out string source,
        out string? packageId,
        out bool packagePresent)
    {
        kind = default;
        source = "";
        packageId = null;
        packagePresent = false;
        language = NormalizeLang(language);

        if (language == "csharp")
            return TryDetectCsharp(plan, out kind, out source, out packageId, out packagePresent);
        if (language == "typescript")
            return TryDetectTypescript(plan, out kind, out source, out packageId, out packagePresent);
        if (language == "python")
            return TryDetectPython(plan, out kind, out source, out packageId, out packagePresent);
        return false;
    }

    private static bool TryDetectCsharp(
        PlanContext plan,
        out TestFrameworkKind kind,
        out string source,
        out string? packageId,
        out bool packagePresent)
    {
        kind = default;
        source = "";
        packageId = null;
        packagePresent = false;
        var hits = new Dictionary<TestFrameworkKind, int>();
        foreach (var proj in EnumerateCsproj(plan))
        {
            string text;
            try { text = File.ReadAllText(proj); }
            catch { continue; }

            foreach (Match m in NugetPackageRef().Matches(text))
            {
                var id = m.Groups["id"].Value;
                if (id.Equals("xunit", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith("xunit.", StringComparison.OrdinalIgnoreCase))
                    hits[TestFrameworkKind.Xunit] = hits.GetValueOrDefault(TestFrameworkKind.Xunit) + 1;
                else if (id.Equals("NUnit", StringComparison.OrdinalIgnoreCase)
                         || id.StartsWith("NUnit.", StringComparison.OrdinalIgnoreCase))
                    hits[TestFrameworkKind.NUnit] = hits.GetValueOrDefault(TestFrameworkKind.NUnit) + 1;
                else if (id.Contains("MSTest", StringComparison.OrdinalIgnoreCase))
                    hits[TestFrameworkKind.MSTest] = hits.GetValueOrDefault(TestFrameworkKind.MSTest) + 1;
            }
        }

        // also scan existing test attrs in .cs under work root (cheap sample)
        try
        {
            foreach (var cs in Directory.EnumerateFiles(plan.WorkRoot, "*Tests.cs", SearchOption.AllDirectories).Take(20))
            {
                var t = File.ReadAllText(cs);
                if (t.Contains("[Fact]", StringComparison.Ordinal) || t.Contains("using Xunit", StringComparison.Ordinal))
                    hits[TestFrameworkKind.Xunit] = hits.GetValueOrDefault(TestFrameworkKind.Xunit) + 2;
                if (t.Contains("[Test]", StringComparison.Ordinal) && t.Contains("NUnit", StringComparison.OrdinalIgnoreCase))
                    hits[TestFrameworkKind.NUnit] = hits.GetValueOrDefault(TestFrameworkKind.NUnit) + 2;
                if (t.Contains("[TestMethod]", StringComparison.Ordinal))
                    hits[TestFrameworkKind.MSTest] = hits.GetValueOrDefault(TestFrameworkKind.MSTest) + 2;
            }
        }
        catch
        {
            // ignore
        }

        if (hits.Count == 0)
            return false;

        kind = hits.OrderByDescending(kv => kv.Value).First().Key;
        source = "detect:nuget+tests";
        packageId = PackageIdFor(kind);
        packagePresent = true;
        return true;
    }

    private static bool TryDetectTypescript(
        PlanContext plan,
        out TestFrameworkKind kind,
        out string source,
        out string? packageId,
        out bool packagePresent)
    {
        kind = default;
        source = "";
        packageId = null;
        packagePresent = false;
        var pkgJson = FindUp(plan.WorkRoot, "package.json") ?? Path.Combine(plan.WorkRoot, "package.json");
        if (!File.Exists(pkgJson))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pkgJson));
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectDeps(doc.RootElement, "dependencies", names);
            CollectDeps(doc.RootElement, "devDependencies", names);
            if (names.Contains("vitest"))
            {
                kind = TestFrameworkKind.Vitest;
                source = "detect:package.json";
                packageId = "vitest";
                packagePresent = true;
                return true;
            }

            if (names.Contains("jest") || names.Contains("@types/jest"))
            {
                kind = TestFrameworkKind.Jest;
                source = "detect:package.json";
                packageId = "jest";
                packagePresent = true;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryDetectPython(
        PlanContext plan,
        out TestFrameworkKind kind,
        out string source,
        out string? packageId,
        out bool packagePresent)
    {
        kind = default;
        source = "";
        packageId = null;
        packagePresent = false;
        foreach (var name in new[] { "pyproject.toml", "requirements.txt", "requirements-dev.txt" })
        {
            var path = Path.Combine(plan.WorkRoot, name);
            if (!File.Exists(path))
                continue;
            try
            {
                var t = File.ReadAllText(path);
                if (t.Contains("pytest", StringComparison.OrdinalIgnoreCase))
                {
                    kind = TestFrameworkKind.Pytest;
                    source = "detect:" + name;
                    packageId = "pytest";
                    packagePresent = true;
                    return true;
                }
            }
            catch
            {
                // continue
            }
        }

        return false;
    }

    public static bool IsPackagePresent(PlanContext plan, string language, string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return true;
        language = NormalizeLang(language);
        if (language == "csharp")
        {
            foreach (var proj in EnumerateCsproj(plan))
            {
                try
                {
                    if (File.ReadAllText(proj).Contains(packageId, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // ignore
                }
            }

            return false;
        }

        if (language == "typescript")
        {
            var pkgJson = FindUp(plan.WorkRoot, "package.json") ?? Path.Combine(plan.WorkRoot, "package.json");
            if (!File.Exists(pkgJson))
                return false;
            try
            {
                return File.ReadAllText(pkgJson).Contains(packageId, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateCsproj(PlanContext plan)
    {
        if (!string.IsNullOrWhiteSpace(plan.SolutionOrProjectPath)
            && plan.SolutionOrProjectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            && File.Exists(plan.SolutionOrProjectPath))
            yield return plan.SolutionOrProjectPath!;

        if (Directory.Exists(plan.WorkRoot))
        {
            foreach (var p in Directory.EnumerateFiles(plan.WorkRoot, "*.csproj", SearchOption.AllDirectories).Take(30))
                yield return p;
        }
    }

    private static void CollectDeps(JsonElement root, string prop, HashSet<string> into)
    {
        if (!root.TryGetProperty(prop, out var deps) || deps.ValueKind != JsonValueKind.Object)
            return;
        foreach (var p in deps.EnumerateObject())
            into.Add(p.Name);
    }

    private static string? FindUp(string start, string fileName)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    private static string NormalizeLang(string? language)
    {
        var lang = (language ?? "").Trim().ToLowerInvariant();
        return lang switch
        {
            "cs" or "c#" or "csharp" or "" => "csharp",
            "ts" or "tsx" or "javascript" or "js" or "typescript" => "typescript",
            "py" or "python" => "python",
            _ => lang
        };
    }
}
