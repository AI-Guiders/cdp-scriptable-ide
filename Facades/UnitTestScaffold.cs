namespace Cdp.ScriptableIde;

internal static class UnitTestScaffold
{
    public const string Kind = "generate.unit_test";

    public static async Task<StepResponse> RunAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string sutAnchor,
        string? testClassName,
        string? outputFilePath,
        TestFrameworkPolicy policy,
        TestFrameworkKind? specified,
        bool ensurePackage,
        CancellationToken ct)
    {
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
        }

        string sutFile;
        string typeName;
        if (!AnchorLocus.TryResolveTypePosition(plan, sutAnchor, Kind, out sutFile, out _, out _, out typeName,
                out var fail))
        {
            if (!AnchorLocus.TryResolveFile(plan, sutAnchor, Kind, out sutFile, out var span, out fail))
                return fail!;
            typeName = span.MemberKey ?? Path.GetFileNameWithoutExtension(sutFile);
        }

        if (resolution.Language != "csharp")
        {
            return StepResponse.Success(Kind, "scaffolded_meta:" + resolution.Kind, new
            {
                note = "non-csharp UnitTest: use TestMethod.Apply for file body",
                framework = resolution.Kind.ToString(),
                language = resolution.Language,
                source = resolution.Source,
                package_ensure = pkgStep
            });
        }

        var className = string.IsNullOrWhiteSpace(testClassName) ? typeName + "Tests" : testClassName!;
        var outFile = outputFilePath;
        if (string.IsNullOrWhiteSpace(outFile))
            outFile = Path.Combine(Path.GetDirectoryName(sutFile)!, className + ".cs");
        else if (!Path.IsPathRooted(outFile))
            outFile = Path.GetFullPath(Path.Combine(plan.WorkRoot, outFile));
        outFile = plan.Resolve(outFile!);

        var ns = TestMethodRunner.GuessNamespace(sutFile);
        var usingFw = resolution.Kind switch
        {
            TestFrameworkKind.NUnit => "using NUnit.Framework;",
            TestFrameworkKind.MSTest => "using Microsoft.VisualStudio.TestTools.UnitTesting;",
            _ => "using Xunit;"
        };
        var classAttr = resolution.Kind == TestFrameworkKind.MSTest ? "[TestClass]\n" : "";
        var text = usingFw + "\n\nnamespace " + ns + ";\n\n" + classAttr + "public class " + className + "\n{\n}\n";

        var args = ScriptArgs.From(new
        {
            sut = sutAnchor,
            path = outFile,
            class_name = className,
            framework = resolution.Kind.ToString(),
            source = resolution.Source
        });
        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(Kind, "dry_run", new { dry_run = true, path = outFile, preview = text });
            bus.RecordLocal("generate", Kind, args, dry.ToJson(), skippedDryRun: true);
            return dry;
        }

        if (File.Exists(outFile))
            return StepResponse.Fail(Kind, "test file already exists", new { path = outFile });

        File.WriteAllText(outFile, text);
        var result = StepResponse.Success(Kind, $"scaffolded:{className}", new
        {
            path = outFile,
            class_name = className,
            sut_type = typeName,
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
}
