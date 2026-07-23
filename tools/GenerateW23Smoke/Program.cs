using Cdp.ScriptableIde;

var dir = Path.Combine(Path.GetTempPath(), "gen-w23-smoke-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var cs = Path.Combine(dir, "Calc.cs");
var csproj = Path.Combine(dir, "Calc.csproj");
await File.WriteAllTextAsync(csproj, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
""");
await File.WriteAllTextAsync(cs, """
namespace Smoke;

public class Calc
{
    public int Value { get; set; }
    public int Add(int a, int b) => a + b;
}
""");

var bus = new ScriptToolBus();
var plan = new PlanContext
{
    PrimaryRoot = dir,
    WorkRoot = dir,
    PlanId = "smoke",
    SolutionOrProjectPath = csproj
};
var g = new ScriptGlobals(bus, plan);
var sut = Anchor.File(cs).Method("Calc");

// W3 UnitTest scaffold (no network in smoke)
var ut = await g.Generate.UnitTestAsync(sut, ensurePackage: false);
Console.WriteLine("UNIT: " + ut.ToJson());
if (!ut.Ok)
    Environment.Exit(1);
var testsPath = Path.Combine(dir, "CalcTests.cs");
if (!File.Exists(testsPath))
    Environment.Exit(2);

// W3 TestMethod + Assertion
var tm = await g.Generate.TestMethod(sut, "Add_ReturnsSum")
    .EnsurePackage(false)
    .AddAssertion(Assertion.Equal("sut.Add(1, 2)", "3"))
    .AddAssertion(Assertion.True("true"))
    .Into(testsPath)
    .ApplyAsync();
Console.WriteLine("TESTMETHOD: " + tm.ToJson());
if (!tm.Ok)
    Environment.Exit(3);
var testsText = await File.ReadAllTextAsync(testsPath);
if (!testsText.Contains("Add_ReturnsSum", StringComparison.Ordinal)
    || !testsText.Contains("Assert.Equal(3, sut.Add(1, 2))", StringComparison.Ordinal)
    || !testsText.Contains("[Fact]", StringComparison.Ordinal))
{
    Console.Error.WriteLine(testsText);
    Environment.Exit(4);
}

// W2 Generate — mocked roslyn
var genBus = new ScriptToolBus(async (domain, tool, args, _) =>
{
    if (domain != "roslyn")
        throw new InvalidOperationException(domain);
    if (tool == "roslyn_generate_constructor_from_members")
    {
        var insert = args.TryGetValue("insert_into_file", out var i) && i.ValueKind == System.Text.Json.JsonValueKind.True;
        if (insert)
        {
            var text = await File.ReadAllTextAsync(cs);
            if (!text.Contains("public Calc(", StringComparison.Ordinal))
            {
                text = text.Replace(
                    "public int Value { get; set; }",
                    "public int Value { get; set; }\n    public Calc(int value) => Value = value;",
                    StringComparison.Ordinal);
                await File.WriteAllTextAsync(cs, text);
            }

            return "inserted constructor into Calc";
        }

        return "public Calc(int value) => Value = value;";
    }

    if (tool == "roslyn_move_members_to_partial_file")
        return "Moved members to partial (preview)";

    throw new InvalidOperationException(tool);
});
var g2 = new ScriptGlobals(genBus, plan);
var ctor = await g2.Generate.Constructor.At(sut).Members("Value").ApplyAsync();
Console.WriteLine("CTOR: " + ctor.ToJson());
if (!ctor.Ok)
    Environment.Exit(5);
if (!(await File.ReadAllTextAsync(cs)).Contains("public Calc(int value)", StringComparison.Ordinal))
    Environment.Exit(6);

var move = await g2.Refactor.Move.MembersToPartial.At(sut)
    .Members("Add")
    .ToFile(Path.Combine(dir, "Calc.Add.cs"))
    .PreviewOnly()
    .ApplyAsync();
Console.WriteLine("MOVE: " + move.ToJson());
if (!move.Ok)
    Environment.Exit(7);

var nunitFile = Path.Combine(dir, "CalcNUnitTests.cs");
var nunit = await g.Generate.TestMethod(sut, "NUnit_Equal")
    .Framework(TestFrameworkKind.NUnit)
    .EnsurePackage(false)
    .AddAssertion(Assertion.Equal("x", "1"))
    .Into(nunitFile)
    .ApplyAsync();
Console.WriteLine("NUNIT: " + nunit.ToJson());
if (!nunit.Ok)
    Environment.Exit(8);
if (!(await File.ReadAllTextAsync(nunitFile)).Contains("Is.EqualTo", StringComparison.Ordinal))
    Environment.Exit(9);

Console.WriteLine("OK");
