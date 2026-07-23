using Cdp.ScriptableIde;

var work = Path.Combine(Path.GetTempPath(), "settings-smoke-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
var bus = new ScriptToolBus();
var plan = new PlanContext
{
    PrimaryRoot = work,
    WorkRoot = work,
    PlanId = "smoke",
    Language = "csharp"
};
ProjectSettingsLoader.Hydrate(plan);
var g = new ScriptGlobals(bus, plan);

Console.WriteLine("HYDRATE1 fw=" + g.Settings.TestFramework + " src=" + g.Settings.TestFrameworkSource);
if (g.Settings.TestFramework is null)
{
    Console.Error.WriteLine("expected Detect/fallback after hydrate");
    Environment.Exit(1);
}

var set = await g.Settings.SetTestFrameworkAsync(TestFrameworkKind.NUnit);
Console.WriteLine("SET: " + set.ToJson());
if (!set.Ok) Environment.Exit(2);

var toml = Path.Combine(work, ".cdp", "project.toml");
if (!File.Exists(toml))
{
    Console.Error.WriteLine("missing " + toml);
    Environment.Exit(3);
}

var text = File.ReadAllText(toml);
Console.WriteLine("TOML:\n" + text);
if (!text.Contains("nunit", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("toml missing nunit");
    Environment.Exit(4);
}

// fresh plan = re-hydrate from file
var plan2 = new PlanContext
{
    PrimaryRoot = work,
    WorkRoot = work,
    PlanId = "smoke2",
    Language = "csharp"
};
ProjectSettingsLoader.Hydrate(plan2);
if (plan2.Settings.TestFramework != TestFrameworkKind.NUnit
    || plan2.Settings.TestFrameworkPolicy != TestFrameworkPolicy.Specified)
{
    Console.Error.WriteLine("rehydrate expected NUnit Specified, got "
        + plan2.Settings.TestFramework + " " + plan2.Settings.TestFrameworkPolicy);
    Environment.Exit(5);
}

var effective = TestFrameworkResolver.ResolveEffective(plan2, TestFrameworkPolicy.Detect, callSpecified: null);
Console.WriteLine("EFFECTIVE: " + effective.Kind + " src=" + effective.Source);
if (effective.Kind != TestFrameworkKind.NUnit)
{
    Console.Error.WriteLine("ResolveEffective should honor file pin");
    Environment.Exit(6);
}

var g2 = new ScriptGlobals(bus, plan2);
var cleared = await g2.Settings.ClearTestFrameworkAsync();
Console.WriteLine("CLEAR: " + cleared.ToJson());
if (!cleared.Ok) Environment.Exit(7);

var afterClear = File.ReadAllText(toml);
Console.WriteLine("TOML after clear:\n" + afterClear);
if (afterClear.Contains("framework", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("clear should not persist framework pin");
    Environment.Exit(8);
}

var plan3 = new PlanContext
{
    PrimaryRoot = work,
    WorkRoot = work,
    PlanId = "smoke3",
    Language = "csharp"
};
ProjectSettingsLoader.Hydrate(plan3);
if (plan3.Settings.TestFrameworkPolicy != TestFrameworkPolicy.Detect)
{
    Console.Error.WriteLine("expected Detect after clear rehydrate");
    Environment.Exit(9);
}

Console.WriteLine("OK work=" + work);
