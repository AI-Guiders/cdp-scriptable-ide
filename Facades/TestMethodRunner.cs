namespace Cdp.ScriptableIde;

/// <summary>Orchestrate TestMethod Apply: FW resolve, package, thin language dispatch.</summary>
internal static class TestMethodRunner
{
    public const string Kind = "generate.test_method";

    public static async Task<StepResponse> ApplyAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string sutAnchor,
        string name,
        IReadOnlyList<ArrangeIntent> arranges,
        IReadOnlyList<ActIntent> acts,
        IReadOnlyList<AssertionIntent> assertions,
        string? testClassFile,
        TestFrameworkPolicy policy,
        TestFrameworkKind? specified,
        bool ensurePackage,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return StepResponse.Fail(Kind, "test method name is required");

        TestFrameworkResolution resolution;
        try
        {
            resolution = TestFrameworkResolver.ResolveEffective(plan, policy, specified);
        }
        catch (Exception ex)
        {
            return StepResponse.Fail(Kind, ex.Message);
        }

        StepResponse? pkgStep = null;
        if (ensurePackage)
        {
            if (bus is not ScriptToolBus concreteBus)
                return StepResponse.Fail(Kind, "EnsurePackage requires ScriptToolBus");
            var (last, added) = await TestPackageBundle.EnsureAsync(concreteBus, plan, resolution.Kind, ct)
                .ConfigureAwait(false);
            if (last is { Ok: false })
                return StepResponse.Fail(Kind, "ensure package failed: " + last.Error, new { package = last, added });
            pkgStep = last;
            if (added.Count > 0 || TestFrameworkResolver.IsPackagePresent(plan, resolution.Language, resolution.PackageId))
                resolution = resolution with { PackagePresent = true };
        }

        return resolution.Language switch
        {
            "csharp" => await CsharpTestEmitter.ApplyAsync(
                bus, plan, sutAnchor, name, arranges, acts, assertions, testClassFile, resolution, pkgStep, ct)
                .ConfigureAwait(false),
            "typescript" => TypescriptTestEmitter.Apply(
                bus, plan, sutAnchor, name, arranges, acts, assertions, testClassFile, resolution, pkgStep),
            "python" => PythonTestEmitter.Apply(
                bus, plan, sutAnchor, name, arranges, acts, assertions, testClassFile, resolution, pkgStep),
            _ => StepResponse.Fail(Kind, $"no test projection for language '{resolution.Language}'")
        };
    }

    internal static Task<StepResponse> WriteOrAppendAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string outFile,
        string usingFw,
        string ns,
        string className,
        string method,
        string name,
        string sutType,
        TestFrameworkResolution resolution,
        StepResponse? pkgStep)
    {
        var args = ScriptArgs.From(new
        {
            name,
            file = outFile,
            framework = resolution.Kind.ToString(),
            policy_source = resolution.Source
        });

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(Kind, "dry_run", new
            {
                dry_run = true,
                path = outFile,
                method = name,
                framework = resolution.Kind.ToString(),
                source = resolution.Source
            });
            bus.RecordLocal("generate", Kind, args, dry.ToJson(), skippedDryRun: true);
            return Task.FromResult(dry);
        }

        string newText;
        if (!File.Exists(outFile))
        {
            newText = usingFw + "\n\nnamespace " + ns + ";\n\npublic class " + className + "\n{" + method + "}\n";
        }
        else
        {
            var existing = File.ReadAllText(outFile);
            if (existing.Contains($"void {name}(", StringComparison.Ordinal))
                return Task.FromResult(StepResponse.Fail(Kind, $"method {name} already exists", new { path = outFile }));
            var insertAt = existing.LastIndexOf('}');
            if (insertAt < 0)
                return Task.FromResult(StepResponse.Fail(Kind, "cannot find class closing brace", new { path = outFile }));
            newText = existing[..insertAt] + method + "\n" + existing[insertAt..];
            if (!string.IsNullOrEmpty(usingFw) && !existing.Contains(usingFw, StringComparison.Ordinal))
                newText = usingFw + "\n" + newText;
        }

        File.WriteAllText(outFile, newText);
        var result = StepResponse.Success(Kind, $"added:{name}", new
        {
            path = outFile,
            method = name,
            sut_type = sutType,
            framework = resolution.Kind.ToString(),
            language = resolution.Language,
            source = resolution.Source,
            package_id = resolution.PackageId,
            package_ensure = pkgStep,
            work_root = plan.WorkRoot
        });
        bus.RecordLocal("generate", Kind, args, result.ToJson(), skippedDryRun: false);
        return Task.FromResult(result);
    }

    internal static StepResponse WriteNewOrFail(
        IScriptToolBus bus,
        PlanContext plan,
        string outFile,
        string text,
        string name,
        string sutType,
        TestFrameworkResolution resolution,
        StepResponse? pkgStep)
    {
        var args = ScriptArgs.From(new { name, file = outFile, framework = resolution.Kind.ToString() });
        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(Kind, "dry_run", new { dry_run = true, path = outFile, preview = text });
            bus.RecordLocal("generate", Kind, args, dry.ToJson(), skippedDryRun: true);
            return dry;
        }

        if (File.Exists(outFile))
            return StepResponse.Fail(Kind, "test file already exists — pass Into(new path) or edit", new { path = outFile });

        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllText(outFile, text);
        var result = StepResponse.Success(Kind, $"added:{name}", new
        {
            path = outFile,
            method = name,
            sut_type = sutType,
            framework = resolution.Kind.ToString(),
            language = resolution.Language,
            source = resolution.Source,
            package_id = resolution.PackageId,
            package_ensure = pkgStep,
            work_root = plan.WorkRoot
        });
        bus.RecordLocal("generate", Kind, args, result.ToJson(), skippedDryRun: false);
        return result;
    }

    internal static string ResolveOutFile(PlanContext plan, string? explicitPath, string sutFile, string defaultName)
    {
        string outFile;
        if (string.IsNullOrWhiteSpace(explicitPath))
            outFile = Path.Combine(Path.GetDirectoryName(sutFile) ?? plan.WorkRoot, defaultName);
        else if (!Path.IsPathRooted(explicitPath))
            outFile = Path.GetFullPath(Path.Combine(plan.WorkRoot, explicitPath));
        else
            outFile = Path.GetFullPath(explicitPath);
        return plan.Resolve(outFile);
    }

    internal static string GuessNamespace(string sutFile)
    {
        try
        {
            var text = File.ReadAllText(sutFile);
            var m = System.Text.RegularExpressions.Regex.Match(text, @"namespace\s+([\w.]+)\s*;");
            if (m.Success)
                return m.Groups[1].Value;
            m = System.Text.RegularExpressions.Regex.Match(text, @"namespace\s+([\w.]+)\s*\{");
            if (m.Success)
                return m.Groups[1].Value;
        }
        catch
        {
            // ignore
        }

        return "Tests";
    }
}
