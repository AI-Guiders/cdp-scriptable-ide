using Cdp.ScriptableIde;

var work = @"D:\Experiments\PersonalCursorFolder\Financial\software\open\_dogfood-w23-live";
var bus = new ScriptToolBus();
var plan = new PlanContext
{
    PrimaryRoot = work,
    WorkRoot = work,
    Language = "csharp",
    SolutionOrProjectPath = Path.Combine(work, "Dogfood.csproj")
};
var g = new ScriptGlobals(bus, plan);

var cs = Path.Combine(work, "Counter.cs");
var tests = Path.Combine(work, "CounterTests.cs");
var sut = Anchor.File(cs).Method("Counter");

// Add overload that takes named-ish nothing — Counter.Inc() has no args.
// Exercise Invocation axes + Bind; also Act.Call short path still works via prior tests.
var tm = await g.Generate.TestMethod(sut, "Invocation_Inc_Bind")
    .Into(tests)
    .Arrange(Arrange.Sut("c", "1"))
    .Act(Invocation.On("c").Named("Inc").Bind("got"))
    .AddAssertion(Assertion.Equal("got", "2"))
    .ApplyAsync();
Console.WriteLine("INV " + tm.Ok + " " + tm.Summary);
if (!tm.Ok)
{
    Console.Error.WriteLine(tm.Error);
    Environment.Exit(1);
}

var text = File.ReadAllText(tests);
if (!text.Contains("var got = c.Inc();", StringComparison.Ordinal))
{
    Console.Error.WriteLine(text);
    Environment.Exit(2);
}

// Named-arg projection unit (no compile — just Project via a throwaway CallAct path:
// Counter has no named params; check csharp wire from helper by building Invoke with Arg name.
var inv = Invocation.On("c").Named("Inc").Arg("delta", "1").Bind("x");
var act = inv.ToAct();
if (act is not CallAct ca || ca.Args.Count != 1 || ca.Args[0].Name != "delta")
{
    Console.Error.WriteLine("structured args missing");
    Environment.Exit(3);
}

var psi = new System.Diagnostics.ProcessStartInfo
{
    FileName = "dotnet",
    Arguments = $"test \"{plan.SolutionOrProjectPath}\" --nologo --filter Invocation_Inc_Bind",
    WorkingDirectory = work,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};
using var p = System.Diagnostics.Process.Start(psi)!;
Console.WriteLine(await p.StandardOutput.ReadToEndAsync());
await p.WaitForExitAsync();
if (p.ExitCode != 0) Environment.Exit(4);

Console.WriteLine("OK");
