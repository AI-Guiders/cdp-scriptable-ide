namespace Cdp.ScriptableIde;

/// <summary>TestMethod entity — accumulate arrange/act/assertions, one Apply → wire.</summary>
public sealed class TestMethodEntity(IScriptToolBus bus, PlanContext plan, string sutAnchor, string name)
{
    private readonly List<ArrangeIntent> _arranges = [];
    private readonly List<ActIntent> _acts = [];
    private readonly List<AssertionIntent> _assertions = [];
    private string? _testClassFile;
    private TestFrameworkPolicy _policy = TestFrameworkPolicy.Detect;
    private TestFrameworkKind? _specified;
    private bool _ensurePackage = true;

    public TestMethodEntity Arrange(ArrangeIntent arrange)
    {
        _arranges.Add(arrange);
        return this;
    }

    public TestMethodEntity Arrange(DeclareBuilder declare) => Arrange(declare.ToArrange());

    public TestMethodEntity Act(ActIntent act)
    {
        _acts.Add(act);
        return this;
    }

    public TestMethodEntity Act(CallBuilder call) => Act(call.ToAct());

    public TestMethodEntity Act(InvocationEntity invocation) => Act(invocation.ToAct());

    public TestMethodEntity AddAssertion(AssertionIntent assertion)
    {
        _assertions.Add(assertion);
        return this;
    }

    public TestMethodEntity Into(string testClassFilePath)
    {
        _testClassFile = testClassFilePath;
        return this;
    }

    /// <summary>Pin framework (sets policy = Specified).</summary>
    public TestMethodEntity Framework(TestFrameworkKind framework)
    {
        _specified = framework;
        _policy = TestFrameworkPolicy.Specified;
        return this;
    }

    public TestMethodEntity Policy(TestFrameworkPolicy policy)
    {
        _policy = policy;
        return this;
    }

    /// <summary>When Detect/fallback and package missing — test package bundle.</summary>
    public TestMethodEntity EnsurePackage(bool ensure = true)
    {
        _ensurePackage = ensure;
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        TestMethodRunner.ApplyAsync(
            bus, plan, sutAnchor, name, _arranges, _acts, _assertions, _testClassFile,
            _policy, _specified, _ensurePackage, ct);
}
