using Cdp.ScriptableIde;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var work = Path.Combine(Path.GetTempPath(), "cdp-refactor-k-" + Guid.NewGuid().ToString("N")[..8]);
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
    public double Solve(double a, double b, double c)
    {
        var d = b * b - 4 * a * c;
        return d;
    }

    public void Call()
    {
        _ = Solve(1, 2, 3);
    }
}
""");

// ── K: resolve ──────────────────────────────────────────────────────────────
static void AssertRole(string file, string wire, string expectDetailContains, string expectKind)
{
    var span = BracketLocate.Parse(wire);
    if (!BracketSyntaxResolve.TryFindAttachTarget(file, span, out var target, out var detail))
        throw new Exception($"resolve fail {wire}: {detail}");
    if (!detail.Contains(expectDetailContains, StringComparison.OrdinalIgnoreCase))
        throw new Exception($"detail '{detail}' missing '{expectDetailContains}' for {wire}");
    var kind = target.Node.Kind().ToString();
    if (!kind.Contains(expectKind, StringComparison.OrdinalIgnoreCase)
        && target.Node.GetType().Name.IndexOf(expectKind, StringComparison.OrdinalIgnoreCase) < 0)
        throw new Exception($"node {kind}/{target.Node.GetType().Name} not like {expectKind} for {wire}");
    Console.WriteLine($"K ok {wire} → {detail} ({kind})");
}

var f = file.Replace('\\', '/');
AssertRole(file, $"[F:{f};M:Solve;K:Parameter:a]", "Parameter", "Parameter");
AssertRole(file, $"[F:{f};M:Solve;K:Name]", "Name", "Method");
AssertRole(file, $"[F:{f};M:Solve;K:ReturnType]", "ReturnType", "PredefinedType");
AssertRole(file, $"[F:{f};M:Solve;K:Body]", "Body", "Block");
AssertRole(file, $"[F:{f};M:Solve;K:Type]", "Type", "PredefinedType"); // method Type ≡ ReturnType

// unknown role fail-loud
{
    var span = BracketLocate.Parse($"[F:{f};M:Solve;K:Nope]");
    if (BracketSyntaxResolve.TryFindAttachTarget(file, span, out _, out var detail)
        || !detail.Contains("unknown_role", StringComparison.OrdinalIgnoreCase))
        throw new Exception($"expected unknown_role, got {detail}");
    Console.WriteLine("K unknown_role ok");
}

// fluent helpers round-trip
var a = Anchor.File(file).Method("Solve").Parameter("b");
if (!a.ToWire().Contains("K:Parameter:b", StringComparison.Ordinal))
    throw new Exception("Parameter wire: " + a.ToWire());
Console.WriteLine("Anchor.Parameter wire ok: " + a.ToWire());

// ── Rename.Parameter (mock roslyn) ──────────────────────────────────────────
int? renameLine = null, renameCol = null;
var bus = new ScriptToolBus(async (domain, tool, args, ct) =>
{
    _ = domain;
    _ = ct;
    if (tool == "roslyn_rename")
    {
        renameLine = args["line"].GetInt32();
        renameCol = args["column"].GetInt32();
        return StepResponse.Success("roslyn.rename", "renamed").ToJson();
    }

    if (tool == "roslyn_get_code_actions")
    {
        return StepResponse.Success("roslyn.get_code_actions", "ok", new
        {
            actions = new[]
            {
                new { index = 0, title = "Introduce local" },
                new { index = 1, title = "Inline temporary variable" },
                new { index = 2, title = "Change signature…" }
            }
        }).ToJson();
    }

    if (tool == "roslyn_apply_code_action")
        return StepResponse.Success("roslyn.apply_code_action", "applied").ToJson();
    if (tool == "roslyn_format_document")
        return StepResponse.Success("roslyn.format", "formatted").ToJson();
    if (tool == "roslyn_generate_interface_from_class")
        return StepResponse.Success("roslyn.generate_interface", "generated I").ToJson();
    if (tool == "roslyn_generate_base_class_from_class")
        return StepResponse.Success("roslyn.generate_base", "generated Base").ToJson();
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

var rRename = await g.Refactor.Rename.At(Anchor.File(file).Method("Solve").Parameter("a")).To("alpha").ApplyAsync();
Console.WriteLine("rename param: " + rRename.Ok + " line=" + renameLine + " col=" + renameCol);
if (!rRename.Ok || renameLine is null)
{
    Console.Error.WriteLine(rRename.Error);
    return 10;
}

// caret should land on param identifier `a` in signature
var text = File.ReadAllText(file);
var lines = text.Replace("\r\n", "\n").Split('\n');
var sig = lines[renameLine!.Value - 1];
if (renameCol is null || renameCol < 1 || renameCol > sig.Length
    || sig[renameCol.Value - 1] != 'a')
{
    Console.Error.WriteLine($"rename caret not on 'a': line='{sig}' col={renameCol}");
    return 11;
}

var rInline = await g.Refactor.Inline.At(Anchor.File(file).Method("Solve").Line(5)).ApplyAsync();
Console.WriteLine("inline: " + rInline.Ok + " " + rInline.Summary);
if (!rInline.Ok)
{
    Console.Error.WriteLine(rInline.Error);
    return 12;
}

var rIntro = await g.Refactor.Introduce.Local.At(Anchor.File(file).Method("Solve").Line(5)).Name("disc").ApplyAsync();
Console.WriteLine("introduce: " + rIntro.Ok + " " + rIntro.Summary);
if (!rIntro.Ok)
{
    Console.Error.WriteLine(rIntro.Error);
    return 13;
}

var rIface = await g.Refactor.Extract.Interface.At(Anchor.File(file).Member("S"))
    .Name("IS").Members("Solve").ApplyAsync();
Console.WriteLine("iface: " + rIface.Ok + " " + rIface.Summary);
if (!rIface.Ok)
{
    Console.Error.WriteLine(rIface.Error);
    return 14;
}

var rBase = await g.Refactor.Extract.Base.At(Anchor.File(file).Member("S")).Name("SBase").ApplyAsync();
Console.WriteLine("base: " + rBase.Ok + " " + rBase.Summary);
if (!rBase.Ok)
{
    Console.Error.WriteLine(rBase.Error);
    return 15;
}

// ChangeSignature — real local rewrite
var rSig = await g.Refactor.ChangeSignature.At(Anchor.File(file).Method("Solve"))
    .Add("eps", Types.Double, ParamDirection.In, "0")
    .Move("c").Before("a")
    .ApplyAsync();
Console.WriteLine("change-sig: " + rSig.Ok + " " + rSig.Summary);
if (!rSig.Ok)
{
    Console.Error.WriteLine(rSig.Error);
    return 16;
}

var after = File.ReadAllText(file);
if (!after.Contains("Solve(double c, double a, double b, double eps = 0)", StringComparison.Ordinal)
    && !after.Contains("Solve(double c,double a,double b,double eps=0)", StringComparison.Ordinal))
{
    // tolerate trivia
    if (!after.Contains("double eps", StringComparison.Ordinal)
        || !after.Contains("double c", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("signature rewrite missing:\n" + after);
        return 17;
    }
}

if (!after.Contains("Solve(3, 1, 2)", StringComparison.Ordinal)
    && !after.Contains("Solve(3,1,2)", StringComparison.Ordinal))
{
    Console.Error.WriteLine("call site not reordered:\n" + after);
    return 18;
}

Console.WriteLine("signature body:\n" + after);

try { Directory.Delete(work, true); } catch { /* ignore */ }
Console.WriteLine("OK");
return 0;
