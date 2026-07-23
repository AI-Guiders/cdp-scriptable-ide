using Cdp.ScriptableIde;

var work = Path.Combine(Path.GetTempPath(), "bundle-smoke-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
var bus = new ScriptToolBus();
var plan = new PlanContext { PrimaryRoot = work, WorkRoot = work, PlanId = "smoke", Language = "csharp" };
var g = new ScriptGlobals(bus, plan);

var proj = await g.Projects.CreateAsync(work, name: "BundleLib", template: "classlib", tfmPolicy: TfmPolicy.Latest);
Console.WriteLine("PROJ " + proj.Ok + " " + proj.Summary);
if (!proj.Ok) Environment.Exit(1);
var csproj = proj.Data!.Value.GetProperty("project").GetString()!;
plan = new PlanContext
{
    PrimaryRoot = work,
    WorkRoot = work,
    PlanId = "smoke",
    Language = "csharp",
    SolutionOrProjectPath = csproj
};
g = new ScriptGlobals(bus, plan);

var csprojText = File.ReadAllText(csproj);
if (!csprojText.Contains("**/_*/**", StringComparison.Ordinal))
{
    Console.Error.WriteLine("expected DefaultItemExcludes after Projects.Create");
    Environment.Exit(2);
}

var sutPath = Path.Combine(work, "Sut.cs");
File.WriteAllText(sutPath, "namespace BundleLib;\npublic class Sut { public int N => 1; }\n");
var tests = Path.Combine(work, "SutTests.cs");
var tm = await g.Generate.TestMethod(Anchor.File(sutPath).Method("Sut"), "N_IsOne")
    .Into(tests)
    .AddAssertion(Assertion.Equal("new Sut().N", "1"))
    .ApplyAsync();
Console.WriteLine("TM " + tm.Ok + " " + tm.Summary);
if (!tm.Ok) Environment.Exit(3);

csprojText = File.ReadAllText(csproj);
foreach (var need in new[] { "Microsoft.NET.Test.Sdk", "xunit", "xunit.runner.visualstudio" })
{
    if (!csprojText.Contains(need, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("missing package " + need);
        Environment.Exit(4);
    }
}

// Scratch under TEMP + cleanup via ScriptHost
var report = await ScriptHost.RunAsync(
    """
    var d = Scratch.Create("mstest");
    System.IO.File.WriteAllText(System.IO.Path.Combine(d, "x.txt"), "hi");
    return d;
    """,
    bus, plan, "run");
if (!report.Ok || string.IsNullOrWhiteSpace(report.Result))
{
    Console.Error.WriteLine("scratch run failed: " + report.Error);
    Environment.Exit(5);
}

if (Directory.Exists(report.Result))
{
    Console.Error.WriteLine("scratch should be deleted: " + report.Result);
    Environment.Exit(6);
}

if (report.ScratchesRemoved is null || report.ScratchesRemoved.Count == 0)
{
    Console.Error.WriteLine("expected ScratchesRemoved");
    Environment.Exit(7);
}

// underscore dir under project must not break compile if excludes applied
var junk = Path.Combine(work, "_empty_mstest");
Directory.CreateDirectory(junk);
File.WriteAllText(Path.Combine(junk, "Bad.cs"), "using Microsoft.VisualStudio.TestTools.UnitTesting;\n");

var psi = new System.Diagnostics.ProcessStartInfo
{
    FileName = "dotnet",
    Arguments = $"test \"{csproj}\" --nologo",
    WorkingDirectory = work,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};
using var p = System.Diagnostics.Process.Start(psi)!;
var stdout = await p.StandardOutput.ReadToEndAsync();
var stderr = await p.StandardError.ReadToEndAsync();
await p.WaitForExitAsync();
Console.WriteLine("TEST exit=" + p.ExitCode);
if (p.ExitCode != 0)
{
    Console.Error.WriteLine(stdout + "\n" + stderr);
    Environment.Exit(8);
}

Console.WriteLine("OK work=" + work);
