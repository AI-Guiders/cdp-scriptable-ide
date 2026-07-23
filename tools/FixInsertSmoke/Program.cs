using System.Text.Json;
using Cdp.ScriptableIde;

var dir = Path.Combine(Path.GetTempPath(), "fix-insert-smoke-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var cs = Path.Combine(dir, "Sample.cs");
var csproj = Path.Combine(dir, "Sample.csproj");
await File.WriteAllTextAsync(csproj, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
""");
await File.WriteAllTextAsync(cs, """
namespace Smoke;

public sealed class Sample
{
    public int Add(int a, int b)
    {
        if (a < 0)
            return 0;
        return a + b;
    }

    public object Make() => new List<int>();
}
""");

// --- Insert (in-proc) ---
var insertBus = new ScriptToolBus();
var plan = new PlanContext
{
    PrimaryRoot = dir,
    WorkRoot = dir,
    PlanId = "smoke",
    SolutionOrProjectPath = csproj
};
var g = new ScriptGlobals(insertBus, plan);

var afterIf = await g.Insert
    .After(Anchor.File(cs).Method("Add").If(1))
    .WithText("\n        // inserted-after-if\n")
    .ApplyAsync();
Console.WriteLine("INSERT: " + afterIf.ToJson());
if (!afterIf.Ok)
    Environment.Exit(1);
var afterText = await File.ReadAllTextAsync(cs);
if (!afterText.Contains("inserted-after-if", StringComparison.Ordinal))
    Environment.Exit(2);

// --- Fix (mocked roslyn bus) ---
const string listRaw = """
0	Extract method
1	System.Collections.Generic.List
2	Generate type 'List' > Generate class 'List'
6	using System.Collections.Generic;
""";
var applySeen = false;
var fixBus = new ScriptToolBus(async (domain, tool, args, _) =>
{
    if (domain != "roslyn")
        throw new InvalidOperationException(domain);
    if (tool == "roslyn_get_code_actions")
        return listRaw;
    if (tool == "roslyn_apply_code_action")
    {
        applySeen = true;
        var idx = args["action_index"].GetInt32();
        if (idx != 6)
            return "wrong index " + idx;
        // mutate file like real apply
        var text = await File.ReadAllTextAsync(cs);
        if (!text.Contains("using System.Collections.Generic", StringComparison.Ordinal))
            await File.WriteAllTextAsync(cs, "using System.Collections.Generic;\n" + text);
        return "Applied: using System.Collections.Generic;\nFiles updated.";
    }

    throw new InvalidOperationException(tool);
});

var fixGlobals = new ScriptGlobals(fixBus, plan);
var diagLine =
    $"{cs}:24:38 error CS0246 — The type or namespace name 'List<>' could not be found";
// line number may drift after insert — use Prefer path with explicit At(file,line,col)
var line = 0;
var lines = await File.ReadAllLinesAsync(cs);
for (var i = 0; i < lines.Length; i++)
{
    if (lines[i].Contains("new List<int>", StringComparison.Ordinal))
    {
        line = i + 1;
        break;
    }
}

if (!IdeDiagnostic.TryParse(
        $@"C:\tmp\x.cs:3:38 error CS0246 — The type or namespace name 'List<>' could not be found",
        out var parsed)
    || parsed.Id != "CS0246" || parsed.Line != 3)
{
    Console.Error.WriteLine("parse fail");
    Environment.Exit(3);
}

var fix = await fixGlobals.Fix
    .At(cs, line, Math.Max(1, lines[line - 1].IndexOf("List", StringComparison.Ordinal) + 1), "CS0246")
    .TitleContains("using System.Collections.Generic")
    .ApplyAsync();
Console.WriteLine("FIX: " + fix.ToJson());
if (!fix.Ok || !applySeen)
    Environment.Exit(4);
if (!(await File.ReadAllTextAsync(cs)).Contains("using System.Collections.Generic", StringComparison.Ordinal))
    Environment.Exit(5);

var allDoc = await fixGlobals.Fix.All.Document(cs, line, 1).TitleContains("using").ApplyAsync();
Console.WriteLine("FIX_ALL_DOC: " + allDoc.ToJson());
if (!allDoc.Ok)
    Environment.Exit(6);
if (allDoc.Data is { } data
    && data.TryGetProperty("fix_all_scope", out var scope)
    && scope.GetString() != "document")
    Environment.Exit(7);

_ = diagLine;
Console.WriteLine("OK");
