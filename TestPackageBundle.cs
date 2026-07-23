namespace Cdp.ScriptableIde;

/// <summary>
/// C# test run needs more than the FW package — Sdk + adapter or <c>dotnet test</c> sees 0 tests / build fails.
/// </summary>
public static class TestPackageBundle
{
    public const string NetTestSdk = "Microsoft.NET.Test.Sdk";

    public static IReadOnlyList<string> PackagesFor(TestFrameworkKind kind) => kind switch
    {
        TestFrameworkKind.Xunit => [NetTestSdk, "xunit", "xunit.runner.visualstudio"],
        TestFrameworkKind.NUnit => [NetTestSdk, "NUnit", "NUnit3TestAdapter"],
        TestFrameworkKind.MSTest => [NetTestSdk, "MSTest.TestFramework", "MSTest.TestAdapter"],
        _ => TestFrameworkResolver.PackageIdFor(kind) is { } one ? [one] : []
    };

    /// <summary>Add missing packages; also keep underscore scratch dirs out of SDK globs.</summary>
    public static async Task<(StepResponse? Last, IReadOnlyList<StepResponse> Added)> EnsureAsync(
        ScriptToolBus bus,
        PlanContext plan,
        TestFrameworkKind kind,
        CancellationToken ct)
    {
        var lang = TestFrameworkResolver.LanguageFor(kind);
        if (lang != "csharp")
        {
            var id = TestFrameworkResolver.PackageIdFor(kind);
            if (id is null || TestFrameworkResolver.IsPackagePresent(plan, lang, id))
                return (null, []);
            var one = await PackageOps.AddAsync(bus, plan, id, version: null, ct: ct).ConfigureAwait(false);
            return (one, one.Ok ? [one] : []);
        }

        TryExcludeUnderscoreScratchDirs(plan);

        var added = new List<StepResponse>();
        StepResponse? last = null;
        foreach (var pkg in PackagesFor(kind))
        {
            if (TestFrameworkResolver.IsPackagePresent(plan, "csharp", pkg))
                continue;
            last = await PackageOps.AddAsync(bus, plan, pkg, version: null, ct: ct).ConfigureAwait(false);
            added.Add(last);
            if (!last.Ok)
                return (last, added);
        }

        return (last, added);
    }

    /// <summary>
    /// SDK-style projects compile every <c>**/*.cs</c> under the csproj dir — agent scratch
    /// folders like <c>_empty_mstest</c> must not land on the compile graph.
    /// </summary>
    public static bool TryExcludeUnderscoreScratchDirs(PlanContext plan)
    {
        var csproj = plan.SolutionOrProjectPath;
        if (string.IsNullOrWhiteSpace(csproj)
            || !csproj.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(csproj))
        {
            csproj = Directory.Exists(plan.WorkRoot)
                ? Directory.EnumerateFiles(plan.WorkRoot, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault()
                : null;
        }

        if (string.IsNullOrWhiteSpace(csproj) || !File.Exists(csproj))
            return false;

        try
        {
            var text = File.ReadAllText(csproj);
            if (text.Contains("**/_*/**", StringComparison.Ordinal)
                || text.Contains("**/_*/*", StringComparison.Ordinal))
                return false;

            const string needle = "<PropertyGroup>";
            var idx = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return false;

            var insertAt = idx + needle.Length;
            var patch =
                "\n    <!-- CDP: keep _scratch dirs out of compile (agent probes → TEMP via Scratch.*) -->\n" +
                "    <DefaultItemExcludes>$(DefaultItemExcludes);**/_*/**</DefaultItemExcludes>";
            File.WriteAllText(csproj, text.Insert(insertAt, patch));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
