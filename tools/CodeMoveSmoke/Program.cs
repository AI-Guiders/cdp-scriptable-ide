using Cdp.ScriptableIde;

var work = Path.Combine(Path.GetTempPath(), "cdp-code-move-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
var bus = new ScriptToolBus();
var plan = new PlanContext
{
    PrimaryRoot = work,
    WorkRoot = work,
    Language = "csharp",
    SolutionOrProjectPath = work
};
var g = new ScriptGlobals(bus, plan);

// --- relocate lines ---
var file = Path.Combine(work, "M.cs");
File.WriteAllText(file, """
namespace T;
public static class M
{
    public static int F()
    {
        var a = 1;
        var b = 2;
        return a + b;
    }
}
""");

var from = Anchor.File(file).Lines(7, 7);
var to = Anchor.File(file).Lines(6, 6);
var r = await g.Code.Move().From(from).To(to).Before().ApplyAsync();
Console.WriteLine("relocate: " + r.Ok + " " + r.Summary);
if (!r.Ok) { Console.Error.WriteLine(r.Error); return 1; }
var text = File.ReadAllText(file);
if (text.IndexOf("var b = 2", StringComparison.Ordinal) > text.IndexOf("var a = 1", StringComparison.Ordinal))
{
    Console.Error.WriteLine("relocate order wrong");
    return 2;
}

static async Task<int> FoldCase(
    ScriptGlobals g,
    string work,
    string name,
    string source,
    Func<string, Anchor> condFactory,
    string expectNeedle)
{
    var path = Path.Combine(work, name + ".cs");
    File.WriteAllText(path, source);
    var expr = Anchor.File(path).Line(8);
    var cond = condFactory(path);
    var rr = await g.Code.Move().From(expr).To(cond).And().ApplyAsync();
    Console.WriteLine(name + ": " + rr.Ok + " " + rr.Summary);
    if (!rr.Ok)
    {
        Console.Error.WriteLine(rr.Error);
        return 10;
    }

    var folded = File.ReadAllText(path);
    Console.WriteLine(folded);
    if (!folded.Contains(expectNeedle, StringComparison.Ordinal))
    {
        Console.Error.WriteLine(name + " fold wrong");
        return 11;
    }

    if (folded.Contains("\n        IsSmth();\n", StringComparison.Ordinal)
        || folded.Contains("\n        IsSmth();\r\n", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(name + " source not removed");
        return 12;
    }

    return 0;
}

var ifRc = await FoldCase(g, work, "If", """
namespace T;
public class D
{
    public bool IsSmth() => true;

    public void Run(int x)
    {
        IsSmth();
        if (x == 0)
        {
        }
    }
}
""", p => Anchor.File(p).Method("Run").If().Condition(), "x == 0 && IsSmth()");
if (ifRc != 0) return ifRc;

var whileRc = await FoldCase(g, work, "While", """
namespace T;
public class D
{
    public bool IsSmth() => true;

    public void Run(int x)
    {
        IsSmth();
        while (x == 0)
        {
        }
    }
}
""", p => Anchor.File(p).Method("Run").While().Condition(), "x == 0 && IsSmth()");
if (whileRc != 0) return whileRc;

var forRc = await FoldCase(g, work, "For", """
namespace T;
public class D
{
    public bool IsSmth() => true;

    public void Run(int x)
    {
        IsSmth();
        for (; x == 0; )
        {
        }
    }
}
""", p => Anchor.File(p).Method("Run").For().Condition(), "x == 0 && IsSmth()");
if (forRc != 0) return forRc;

var fe = Path.Combine(work, "ForEach.cs");
File.WriteAllText(fe, """
namespace T;
public class D
{
    public void Run(int[] xs)
    {
        foreach (var x in xs)
        {
        }
    }
}
""");
var feCond = Anchor.File(fe).Method("Run").Foreach().Condition();
if (BracketSyntaxResolve.TryResolve(fe, feCond.ToSpan(), out _, out var feBadDetail))
{
    Console.Error.WriteLine("foreach Condition should fail, got " + feBadDetail);
    return 20;
}

Console.WriteLine("foreach Condition refused (ok)");

var feExpr = Anchor.File(fe).Method("Run").Foreach().Expression();
if (!BracketSyntaxResolve.TryResolve(fe, feExpr.ToSpan(), out var feRange, out var feOkDetail))
{
    Console.Error.WriteLine("foreach Expression resolve failed");
    return 21;
}

Console.WriteLine($"foreach Expression ok ({feOkDetail}) L{feRange.LineStart}:{feRange.ColumnStart}");

var initRc = await FoldCase(g, work, "Initializer", """
namespace T;
public class D
{
    public bool IsSmth() => true;

    public void Run(int bla)
    {
        IsSmth();
        var isExample = bla == 0;
    }
}
""", p => Anchor.File(p).Line(9).Initializer(), "bla == 0 && IsSmth()");
if (initRc != 0) return initRc;

try { Directory.Delete(work, true); } catch { /* ignore */ }
Console.WriteLine("OK");
return 0;
