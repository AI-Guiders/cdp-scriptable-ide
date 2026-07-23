using Cdp.ScriptableIde;

var work = Path.Combine(Path.GetTempPath(), "sln-smoke-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
var bus = new ScriptToolBus();
var plan = new PlanContext { PrimaryRoot = work, WorkRoot = work, PlanId = "smoke" };
var g = new ScriptGlobals(bus, plan);

var slnStep = await g.Solutions.CreateAsync(work, name: "SmokeSln");
Console.WriteLine("SLN: " + slnStep.ToJson());
if (!slnStep.Ok) Environment.Exit(1);

var slnPath = slnStep.Data!.Value.GetProperty("solution").GetString()!;
plan = new PlanContext
{
    PrimaryRoot = work,
    WorkRoot = work,
    PlanId = "smoke",
    SolutionOrProjectPath = slnPath
};
g = new ScriptGlobals(bus, plan);

var projDir = Path.Combine(work, "Lib");
var proj = await g.Projects.CreateAsync(projDir, name: "Lib", template: "classlib",
    tfmPolicy: TfmPolicy.Latest);
Console.WriteLine("PROJ: " + proj.ToJson());
if (!proj.Ok) Environment.Exit(2);
var csproj = proj.Data!.Value.GetProperty("project").GetString()!;

var add = await g.Solutions.AddAsync(csproj, solution: slnPath);
Console.WriteLine("ADD: " + add.ToJson());
if (!add.Ok) Environment.Exit(3);

var listed = await g.Solutions.ListProjectsAsync(slnPath);
Console.WriteLine("LIST: " + listed.ToJson());
if (!listed.Ok) Environment.Exit(4);
var projects = listed.Data!.Value.GetProperty("projects");
if (projects.GetArrayLength() < 1)
{
    Console.Error.WriteLine("expected ≥1 project in sln");
    Environment.Exit(5);
}

var viaProjects = await g.Projects.AddToSlnAsync(csproj, solution: slnPath); // idempotent-ish
Console.WriteLine("ADD2: " + viaProjects.ToJson());

var remove = await g.Solutions.RemoveAsync(csproj, solution: slnPath);
Console.WriteLine("REM: " + remove.ToJson());
if (!remove.Ok) Environment.Exit(6);

var empty = await g.Solutions.ListProjectsAsync(slnPath);
Console.WriteLine("EMPTY: " + empty.ToJson());
if (empty.Data!.Value.GetProperty("projects").GetArrayLength() != 0)
{
    Console.Error.WriteLine("expected 0 projects after remove");
    Environment.Exit(7);
}

Console.WriteLine("OK work=" + work);
