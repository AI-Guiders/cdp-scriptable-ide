namespace Cdp.ScriptableIde;

/// <summary>Python test file envelope (thin — new file only).</summary>
internal static class PythonTestEmitter
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
            sutFile = Path.Combine(plan.WorkRoot, "sut.py");

        var baseName = Path.GetFileNameWithoutExtension(sutFile);
        var sutType = span?.MemberKey ?? baseName;
        if (!TestBodyProjection.TryBuildBody("python", plan, sutType, arranges, acts, assertions, resolution.Kind,
                indent: "    ", emptyHint: "pass  # arrange/act",
                out var body, out var bodyErr))
            return StepResponse.Fail(kind, bodyErr!);

        var outFile = TestMethodRunner.ResolveOutFile(plan, testClassFile, sutFile, "test_" + baseName + ".py");
        var fn = "test_" + SanitizePyIdent(name);
        var text = resolution.Kind == TestFrameworkKind.Unittest
            ? "import unittest\n\nclass Test" + SanitizePyIdent(baseName) + "(unittest.TestCase):\n    def " + fn +
              "(self):\n" + body + "\n\nif __name__ == '__main__':\n    unittest.main()\n"
            : "def " + fn + "():\n" + body + "\n";
        return TestMethodRunner.WriteNewOrFail(bus, plan, outFile, text, name, baseName, resolution, pkgStep);
    }

    private static string SanitizePyIdent(string name)
    {
        var s = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_'));
        if (s.Length == 0 || char.IsDigit(s[0]))
            s = "t_" + s;
        return s;
    }
}
