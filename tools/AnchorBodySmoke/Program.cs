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
var before = File.ReadAllText(cs);

// Anchor rename smoke + Body.AddCondition on Inc
var at = Anchor.File(cs).Method("Inc");
var wire = at.ToWire();
if (!wire.Contains("M:Inc", StringComparison.Ordinal))
{
    Console.Error.WriteLine("bad wire " + wire);
    Environment.Exit(1);
}

var cond = await g.Body.At(at)
    .AddCondition()
    .When("Value < 0")
    .Then("Value = 0;")
    .AtStart() // guard before existing return (Append would be dead code)
    .ApplyAsync();
Console.WriteLine("COND " + cond.Ok + " " + cond.Summary);
if (!cond.Ok)
{
    Console.Error.WriteLine(cond.Error);
    Environment.Exit(2);
}

var loop = await g.Body.At(Anchor.File(cs).Method("Inc"))
    .AddLoop()
    .PreCondition("false")
    .Body("// never")
    .AtStart()
    .ApplyAsync();
Console.WriteLine("LOOP " + loop.Ok + " " + loop.Summary);
if (!loop.Ok)
{
    Console.Error.WriteLine(loop.Error);
    Environment.Exit(3);
}

var after = File.ReadAllText(cs);
if (!after.Contains("if (Value < 0)", StringComparison.Ordinal)
    || !after.Contains("while (false)", StringComparison.Ordinal))
{
    Console.Error.WriteLine(after);
    Environment.Exit(4);
}

File.WriteAllText(cs, before);

var tests = Path.Combine(work, "CounterTests.cs");
if (!File.ReadAllText(tests).Contains("AnchorRename_Smoke", StringComparison.Ordinal))
{
    var tm = await g.Generate.TestMethod(Anchor.File(cs).Method("Counter"), "AnchorRename_Smoke")
        .Into(tests)
        .Arrange(Arrange.Sut("c", "0"))
        .Act(Act.Call("c", "Inc", bind: "got"))
        .AddAssertion(Assertion.Equal("got", "1"))
        .ApplyAsync();
    Console.WriteLine("TM " + tm.Ok + " " + tm.Summary);
    if (!tm.Ok) Environment.Exit(5);
}

var psi = new System.Diagnostics.ProcessStartInfo
{
    FileName = "dotnet",
    Arguments = $"test \"{plan.SolutionOrProjectPath}\" --nologo --filter AnchorRename_Smoke",
    WorkingDirectory = work,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};
using var p = System.Diagnostics.Process.Start(psi)!;
Console.WriteLine(await p.StandardOutput.ReadToEndAsync());
await p.WaitForExitAsync();
if (p.ExitCode != 0) Environment.Exit(6);

Console.WriteLine("OK");
