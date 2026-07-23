using System.Text.Json;
using Cdp.ScriptableIde;

var work = Path.Combine(Path.GetTempPath(), "cdp-extract-ft-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
var file = Path.Combine(work, "S.cs");
var csproj = Path.Combine(work, "S.csproj");
File.WriteAllText(csproj, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
""");
File.WriteAllText(file, """
namespace T;
public class S
{
    public void Run(int x)
    {
        var a = x + 1;
        var b = a * 2;
        Console.WriteLine(b);
    }
}
""");

int? sawLine = null, sawEnd = null;
var bus = new ScriptToolBus(async (domain, tool, args, ct) =>
{
    _ = domain;
    _ = ct;
    if (tool == "roslyn_get_code_actions")
    {
        sawLine = args["line"].GetInt32();
        sawEnd = args["end_line"].GetInt32();
        return StepResponse.Success("roslyn.get_code_actions", "ok", new
        {
            actions = new[] { new { index = 0, title = "Extract method" } }
        }).ToJson();
    }

    if (tool == "roslyn_apply_code_action")
        return StepResponse.Success("roslyn.apply_code_action", "applied").ToJson();
    if (tool == "roslyn_rename")
        return StepResponse.Success("roslyn.rename", "renamed").ToJson();
    if (tool == "roslyn_format_document")
        return StepResponse.Success("roslyn.format", "formatted").ToJson();
    return StepResponse.Fail("mock", "unexpected " + tool).ToJson();
});

var plan = new PlanContext
{
    PrimaryRoot = work,
    WorkRoot = work,
    Language = "csharp",
    SolutionOrProjectPath = csproj
};
var g = new ScriptGlobals(bus, plan);

var from = Anchor.File(file).Method("Run").Line(6);
var till = Anchor.File(file).Method("Run").Line(7);
var r = await g.Refactor.Extract.Method.From(from).Till(till).Name("Compute").ApplyAsync();
Console.WriteLine("extract: " + r.Ok + " " + r.Summary);
if (!r.Ok)
{
    Console.Error.WriteLine(r.Error);
    return 5;
}

if (sawLine != 6 || sawEnd != 7)
{
    Console.Error.WriteLine($"roslyn range wrong line={sawLine} end={sawEnd}");
    return 6;
}

sawLine = null;
sawEnd = null;
var r2 = await g.Refactor.Method.Extract.From(from).Till(till).Name("Compute").ApplyAsync();
if (!r2.Ok || sawLine != 6)
{
    Console.Error.WriteLine("alias path failed");
    return 7;
}

var r3 = await g.Refactor.Rename.At(Anchor.File(file).Method("Run")).To("Execute").ApplyAsync();
Console.WriteLine("rename: " + r3.Ok + " " + r3.Summary);
if (!r3.Ok)
{
    Console.Error.WriteLine(r3.Error);
    return 8;
}

try { Directory.Delete(work, true); } catch { /* ignore */ }
Console.WriteLine("OK");
return 0;
