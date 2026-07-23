namespace Cdp.ScriptableIde;

/// <summary>C# test method envelope — Information Expert for type-from-method + append.</summary>
internal static class CsharpTestEmitter
{
    public static Task<StepResponse> ApplyAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string sutAnchor,
        string name,
        IReadOnlyList<ArrangeIntent> arranges,
        IReadOnlyList<ActIntent> acts,
        IReadOnlyList<AssertionIntent> assertions,
        string? testClassFile,
        TestFrameworkResolution resolution,
        StepResponse? pkgStep,
        CancellationToken ct)
    {
        _ = ct;
        const string kind = TestMethodRunner.Kind;
        if (!TestBodyProjection.IsValidIdentifier(name))
            return Task.FromResult(StepResponse.Fail(kind, "test method name must be a C# identifier", new { name }));

        if (!AnchorLocus.TryResolveFile(plan, sutAnchor, kind, out var sutFile, out var span, out var fail))
            return Task.FromResult(fail!);

        // Method anchor → containing type (Arrange.Sut / *Tests.cs); not MemberKey as type name.
        var sutType = span.MemberKey ?? Path.GetFileNameWithoutExtension(sutFile);
        if (AnchorLocus.TryResolveTypePosition(plan, sutAnchor, kind, out _, out _, out _, out var typeFromAnchor, out _))
            sutType = typeFromAnchor;

        if (!TestBodyProjection.TryBuildBody("csharp", plan, sutType, arranges, acts, assertions, resolution.Kind,
                indent: "        ", emptyHint: "// arrange/act — agent fills; assertions optional",
                out var methodBody, out var bodyErr))
            return Task.FromResult(StepResponse.Fail(kind, bodyErr!));

        var outFile = TestMethodRunner.ResolveOutFile(plan, testClassFile, sutFile, sutType + "Tests.cs");
        var ns = TestMethodRunner.GuessNamespace(sutFile);
        var className = Path.GetFileNameWithoutExtension(outFile);

        var (attr, usingFw) = Header(resolution.Kind);
        var method =
            "\n    " + attr + "\n    public void " + name + "()\n    {\n" + methodBody + "\n    }\n";

        return TestMethodRunner.WriteOrAppendAsync(
            bus, plan, outFile, usingFw, ns, className, method, name, sutType, resolution, pkgStep);
    }

    private static (string Attr, string Using) Header(TestFrameworkKind kind) => kind switch
    {
        TestFrameworkKind.NUnit => ("[Test]", "using NUnit.Framework;"),
        TestFrameworkKind.MSTest => ("[TestMethod]", "using Microsoft.VisualStudio.TestTools.UnitTesting;"),
        _ => ("[Fact]", "using Xunit;")
    };
}
