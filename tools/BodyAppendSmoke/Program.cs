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

var py = PredicateProjection.Project("python",
    Predicate.And(Predicate.Lt("d", "0"), Predicate.Not(Predicate.Eq("a", "0"))));
if (py != "d < 0 and not (a == 0)")
{
    Console.Error.WriteLine("py proj: " + py);
    Environment.Exit(5);
}

var qPath = Path.Combine(work, "Quadratic.cs");
File.WriteAllText(qPath, """
namespace Dogfood;

public static class Quadratic
{
    public static string Solve(double a, double b, double c)
    {
        double d = b * b - 4 * a * c;
    }
}
""".TrimStart());

var solve = Anchor.File(qPath).Method("Solve");

async Task Cond(PredicateIntent when, string then)
{
    var r = await g.Body.At(solve).AddCondition().When(when).Then(then).ApplyAsync();
    Console.WriteLine("COND " + PredicateProjection.Project("csharp", when) + " => " + r.Ok + " " + r.Summary);
    if (!r.Ok)
    {
        Console.Error.WriteLine(r.Error);
        Environment.Exit(1);
    }
}

await Cond(Predicate.Lt("d", "0"), "return \"no real roots\";");
await Cond(Predicate.Lt("Math.Abs(d)", "1e-12"), "var x = -b / (2 * a); return $\"one:{x}\";");
await Cond(Predicate.Gt("d", "0"),
    "var s = Math.Sqrt(d); var x1 = (-b - s) / (2 * a); var x2 = (-b + s) / (2 * a); return $\"two:{x1},{x2}\";");

var fin = await g.Body.At(solve)
    .AddCondition()
    .When(Predicate.True)
    .Then("return \"no real roots\";")
    .ApplyAsync();
if (!fin.Ok)
{
    Console.Error.WriteLine(fin.Error);
    Environment.Exit(2);
}

var text = File.ReadAllText(qPath);
var dIdx = text.IndexOf("double d =", StringComparison.Ordinal);
var ifIdx = text.IndexOf("if (d < 0)", StringComparison.Ordinal);
if (dIdx < 0 || ifIdx < 0 || dIdx > ifIdx)
{
    Console.Error.WriteLine("order broken (d must precede if):\n" + text);
    Environment.Exit(3);
}

Console.WriteLine(text);

var psi = new System.Diagnostics.ProcessStartInfo
{
    FileName = "dotnet",
    Arguments = $"test \"{plan.SolutionOrProjectPath}\" --nologo --filter Quadratic_",
    WorkingDirectory = work,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};
using var p = System.Diagnostics.Process.Start(psi)!;
Console.WriteLine(await p.StandardOutput.ReadToEndAsync());
var err = await p.StandardError.ReadToEndAsync();
await p.WaitForExitAsync();
if (p.ExitCode != 0)
{
    Console.Error.WriteLine(err);
    Console.Error.WriteLine(text);
    Environment.Exit(4);
}

Console.WriteLine("OK");
