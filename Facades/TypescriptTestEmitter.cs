namespace Cdp.ScriptableIde;

/// <summary>TypeScript test file envelope (thin — new file only).</summary>
internal static class TypescriptTestEmitter
{
    public static StepResponse Apply(
        IScriptToolBus bus,
        PlanContext plan,
        string sutAnchor,
        string name,
        IReadOnlyList<ArrangeIntent> arranges,
        IReadOnlyList<ActIntent> acts,
        IReadOnlyList<AssertionIntent> assertions,
        string? testClassFile,
        TestFrameworkResolution resolution,
        StepResponse? pkgStep)
    {
        const string kind = TestMethodRunner.Kind;
        if (!AnchorLocus.TryResolveFile(plan, sutAnchor, kind, out var sutFile, out var span, out _)
            || string.IsNullOrWhiteSpace(sutFile))
            sutFile = Path.Combine(plan.WorkRoot, "sut.ts");

        var baseName = Path.GetFileNameWithoutExtension(sutFile);
        var sutType = span?.MemberKey ?? baseName;
        if (!TestBodyProjection.TryBuildBody("typescript", plan, sutType, arranges, acts, assertions, resolution.Kind,
                indent: "  ", emptyHint: "// arrange/act",
                out var body, out var bodyErr))
            return StepResponse.Fail(kind, bodyErr!);

        var outFile = TestMethodRunner.ResolveOutFile(plan, testClassFile, sutFile, baseName + ".test.ts");
        var text = resolution.Kind switch
        {
            TestFrameworkKind.Vitest =>
                "import { describe, it, expect } from 'vitest';\n\ndescribe('" + baseName + "', () => {\n  it('" + name +
                "', () => {\n" + body + "\n  });\n});\n",
            TestFrameworkKind.NodeTest =>
                "import { describe, it } from 'node:test';\nimport assert from 'node:assert/strict';\n\ndescribe('" +
                baseName + "', () => {\n  it('" + name + "', () => {\n" + body + "\n  });\n});\n",
            _ =>
                "describe('" + baseName + "', () => {\n  it('" + name + "', () => {\n" + body + "\n  });\n});\n"
        };
        return TestMethodRunner.WriteNewOrFail(bus, plan, outFile, text, name, baseName, resolution, pkgStep);
    }
}
