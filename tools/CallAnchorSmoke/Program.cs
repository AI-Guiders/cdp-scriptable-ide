using Cdp.ScriptableIde;

var work = @"D:\Experiments\PersonalCursorFolder\Financial\software\open\_dogfood-w23-live";
var bus = new ScriptToolBus();
var plan = new PlanContext
{
    PrimaryRoot = work,
    WorkRoot = work,
    PlanId = "call-anchor",
    Language = "csharp",
    SolutionOrProjectPath = Path.Combine(work, "Dogfood.csproj")
};
ProjectSettingsLoader.Hydrate(plan);
var g = new ScriptGlobals(bus, plan);

var cs = Path.Combine(work, "Counter.cs");
var tests = Path.Combine(work, "CounterTests.cs");
var inc = Anchor.File(cs).Method("Inc");
var tmName = "CallAnchor_Inc_ReturnsFour";

static void RemoveMethodIfPresent(string path, string methodName)
{
    var text = File.ReadAllText(path);
    var marker = $"public void {methodName}()";
    var i = text.IndexOf(marker, StringComparison.Ordinal);
    if (i < 0) return;
    var fact = text.LastIndexOf("[Fact]", i, StringComparison.Ordinal);
    if (fact < 0) fact = i;
    var brace = text.IndexOf('{', i);
    if (brace < 0) return;
    var depth = 0;
    var end = -1;
    for (var p = brace; p < text.Length; p++)
    {
        if (text[p] == '{') depth++;
        else if (text[p] == '}')
        {
            depth--;
            if (depth == 0) { end = p; break; }
        }
    }
    if (end < 0) return;
    var cutEnd = end + 1;
    while (cutEnd < text.Length && (text[cutEnd] == '\r' || text[cutEnd] == '\n')) cutEnd++;
    File.WriteAllText(path, text.Remove(fact, cutEnd - fact));
}

RemoveMethodIfPresent(tests, tmName);
RemoveMethodIfPresent(tests, "CallAnchor_Solve_Static");

var tm = await g.Generate.TestMethod(inc, tmName)
    .Into(tests)
    .Arrange(Arrange.Sut("c", "3"))
    .Act(Act.Call(inc).On("c").Bind("got"))
    .AddAssertion(Assertion.Equal("got", "4"))
    .ApplyAsync();

Console.WriteLine("TM " + tm.Ok + " " + tm.Summary + " " + tm.Error);
if (!tm.Ok) Environment.Exit(1);

var text = File.ReadAllText(tests);
if (!text.Contains("var c = new Counter(3);", StringComparison.Ordinal)
    || !text.Contains("var got = c.Inc();", StringComparison.Ordinal)
    || !text.Contains(tmName, StringComparison.Ordinal))
{
    Console.Error.WriteLine("wire missing Call(Anchor) projection:\n" + text);
    Environment.Exit(2);
}

var qPath = Path.Combine(work, "Quadratic.cs");
if (File.Exists(qPath))
{
    var solve = Anchor.File(qPath).Method("Solve");
    var staticName = "CallAnchor_Solve_Static";
    var tm2 = await g.Generate.TestMethod(solve, staticName)
        .Into(tests)
        .Arrange(Declare.Constant.Name("a").Type(Types.Double).Value("1"))
        .Arrange(Declare.Constant.Name("b").Type(Types.Double).Value("-3"))
        .Arrange(Declare.Constant.Name("c").Type(Types.Double).Value("2"))
        .Act(Act.Call(solve).Arg("a").Arg("b").Arg("c").Bind("got"))
        .AddAssertion(Assertion.Equal("got", "\"two:1,2\""))
        .ApplyAsync();
    Console.WriteLine("TM2 " + tm2.Ok + " " + tm2.Summary + " " + tm2.Error);
    if (!tm2.Ok) Environment.Exit(4);
    var wire = File.ReadAllText(tests);
    if (!wire.Contains("var got = Quadratic.Solve(a, b, c);", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("static Call(Anchor) wire missing:\n" + wire);
        Environment.Exit(5);
    }
}

var psi = new System.Diagnostics.ProcessStartInfo
{
    FileName = "dotnet",
    Arguments = $"test \"{plan.SolutionOrProjectPath}\" --nologo --filter \"FullyQualifiedName~CallAnchor_\"",
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
