using Cdp.ScriptableIde;

var dir = Path.Combine(Path.GetTempPath(), "annotate-smoke-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var cs = Path.Combine(dir, "Sample.cs");
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
}
""");

var bus = new ScriptToolBus();
var plan = new PlanContext { PrimaryRoot = dir, WorkRoot = dir, PlanId = "smoke" };
var g = new ScriptGlobals(bus, plan);

// Structured locate — no wire-string tax
var method = Anchor.File(cs).Method("Add");
var ifClamp = Anchor.File(cs).Method("Add").If(1);

var wireRound = Anchor.Parse(method.ToWire()).ToWire();
if (wireRound != method.ToWire())
{
    Console.Error.WriteLine($"roundtrip fail: {method.ToWire()} vs {wireRound}");
    Environment.Exit(10);
}

var doc = await g.Annotate.DocComment
    .At(method)
    .Summary("Adds two integers.")
    .Param("a", "Left")
    .Param("b", "Right")
    .Returns("Sum, or 0 if a negative")
    .ApplyAsync();
Console.WriteLine("DOC: " + doc.ToJson());

var comment = await g.Annotate.Comment
    .At(ifClamp)
    .WithText("why: clamp negative left")
    .ApplyAsync();
Console.WriteLine("COMMENT: " + comment.ToJson());

// Escape still works
var escapeWire = $"[F:{cs};M:Add]";
_ = BracketLocate.Parse(escapeWire);

Console.WriteLine("--- file ---");
Console.WriteLine(await File.ReadAllTextAsync(cs));
Console.WriteLine("WIRE method: " + method.ToWire());
Console.WriteLine("WIRE if: " + ifClamp.ToWire());

if (!doc.Ok || !comment.Ok)
    Environment.Exit(1);
if (!(await File.ReadAllTextAsync(cs)).Contains("<summary>", StringComparison.Ordinal))
    Environment.Exit(2);
if (!(await File.ReadAllTextAsync(cs)).Contains("why: clamp", StringComparison.Ordinal))
    Environment.Exit(3);
Console.WriteLine("OK");
