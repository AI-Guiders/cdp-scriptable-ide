using Cdp.ScriptableIde;

var work = @"D:\Experiments\PersonalCursorFolder\Financial\software\open\_dogfood-w23-live";
var bus = new ScriptToolBus();
var plan = new PlanContext
{
    PrimaryRoot = work,
    WorkRoot = work,
    PlanId = "arrange-act",
    Language = "csharp",
    SolutionOrProjectPath = Path.Combine(work, "Dogfood.csproj")
};
ProjectSettingsLoader.Hydrate(plan);
var g = new ScriptGlobals(bus, plan);

var cs = Path.Combine(work, "Counter.cs");
var tests = Path.Combine(work, "CounterTests.cs");
var sut = Anchor.File(cs).Method("Counter");

var tm = await g.Generate.TestMethod(sut, "Inc_FromThree_ReturnsFour")
    .Into(tests)
    .Arrange(Arrange.Sut("c", "3"))
    .Act(Act.Call("c", "Inc", bind: "got"))
    .AddAssertion(Assertion.Equal("got", "4"))
    .AddAssertion(Assertion.Equal("c.Value", "4"))
    .ApplyAsync();

Console.WriteLine("TM " + tm.Ok + " " + tm.Summary + " " + tm.Error);
if (!tm.Ok) Environment.Exit(1);

var text = File.ReadAllText(tests);
if (!text.Contains("var c = new Counter(3);", StringComparison.Ordinal)
    || !text.Contains("var got = c.Inc();", StringComparison.Ordinal)
    || !text.Contains("Inc_FromThree_ReturnsFour", StringComparison.Ordinal))
{
    Console.Error.WriteLine("wire missing arrange/act:\n" + text);
    Environment.Exit(2);
}

var psi = new System.Diagnostics.ProcessStartInfo
{
    FileName = "dotnet",
    Arguments = $"test \"{plan.SolutionOrProjectPath}\" --nologo --filter Inc_FromThree_ReturnsFour",
    WorkingDirectory = work,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};
using var p = System.Diagnostics.Process.Start(psi)!;
var stdout = await p.StandardOutput.ReadToEndAsync();
var stderr = await p.StandardError.ReadToEndAsync();
await p.WaitForExitAsync();
Console.WriteLine(stdout);
if (p.ExitCode != 0)
{
    Console.Error.WriteLine(stderr);
    Environment.Exit(3);
}

Console.WriteLine("OK");
