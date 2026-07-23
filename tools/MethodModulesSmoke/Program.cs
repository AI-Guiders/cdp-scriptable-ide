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

var qPath = Path.Combine(work, "Quadratic.cs");
var td = await g.TypeDecl.Create("Quadratic").Static().Namespace("Dogfood").Into(qPath).Replace().ApplyAsync();
Console.WriteLine("TYPE " + td.Ok + " " + td.Summary);
if (!td.Ok) Environment.Exit(1);

var imp = await g.Modules.Import("System").Into(qPath).ApplyAsync();
Console.WriteLine("IMP " + imp.Ok + " " + imp.Summary);
if (!imp.Ok) Environment.Exit(2);

var typeA = Anchor.File(qPath).Member("Quadratic");
var m = await g.Method.Create("Solve")
    .In(typeA)
    .Static()
    .Returns(Types.String)
    .Param("a", Types.Double)
    .Param("b", Types.Double)
    .Param("c", Types.Double)
    .ApplyAsync();
Console.WriteLine("METH " + m.Ok + " " + m.Summary);
if (!m.Ok) Environment.Exit(3);

var a = Expr.Id("a");
var b = Expr.Id("b");
var c = Expr.Id("c");
var d = Expr.Id("d");
var s = Expr.Id("s");
var math = Expr.Id("Math");

var solve = Anchor.File(qPath).Method("Solve");
var dcl = await g.Body.At(solve)
    .AddDeclare(Declare.Variable.Name("d").Type(Types.Double)
        .Value(Expr.Sub(Expr.Mul(b, b), Expr.Mul(Expr.Lit(4), Expr.Mul(a, c)))))
    .ApplyAsync();
Console.WriteLine("DECL " + dcl.Ok + " " + dcl.Summary);
if (!dcl.Ok) Environment.Exit(4);

async Task Cond(PredicateIntent when, params StmtIntent[] then)
{
    var r = await g.Body.At(solve).AddCondition().When(when).Then(then).ApplyAsync();
    if (!r.Ok)
    {
        Console.Error.WriteLine(r.Error);
        Environment.Exit(5);
    }
}

await Cond(Predicate.Lt(d, Expr.Lit(0)), Stmt.ReturnLit("no real roots"));
await Cond(
    Predicate.Lt(Expr.Call(math, "Abs", d), Expr.Lit(1e-12)),
    Stmt.Declare(Declare.Variable.Name("x").Type(Types.Infer)
        .Value(Expr.Div(Expr.Neg(b), Expr.Mul(Expr.Lit(2), a)))),
    Stmt.ReturnInterp("one:{x}"));
await Cond(
    Predicate.Gt(d, Expr.Lit(0)),
    Stmt.Declare(Declare.Variable.Name("s").Type(Types.Infer).Value(Expr.Call(math, "Sqrt", d))),
    Stmt.Declare(Declare.Variable.Name("x1").Type(Types.Infer)
        .Value(Expr.Div(Expr.Sub(Expr.Neg(b), s), Expr.Mul(Expr.Lit(2), a)))),
    Stmt.Declare(Declare.Variable.Name("x2").Type(Types.Infer)
        .Value(Expr.Div(Expr.Add(Expr.Neg(b), s), Expr.Mul(Expr.Lit(2), a)))),
    Stmt.ReturnInterp("two:{x1},{x2}"));
await Cond(Predicate.True, Stmt.ReturnLit("no real roots"));

var text = File.ReadAllText(qPath);
Console.WriteLine(text);
if (!text.Contains("b * b - 4 * a * c", StringComparison.Ordinal)
    || !text.Contains("Math.Abs(d)", StringComparison.Ordinal)
    || !text.Contains("Math.Sqrt(d)", StringComparison.Ordinal)
    || !text.Contains("-b / (2 * a)", StringComparison.Ordinal)
    || !text.Contains("(-b - s) / (2 * a)", StringComparison.Ordinal))
{
    Console.Error.WriteLine("expr projection unexpected");
    Environment.Exit(8);
}

var psi = new System.Diagnostics.ProcessStartInfo
{
    FileName = "dotnet",
    Arguments = $"test \"{plan.SolutionOrProjectPath}\" --nologo --filter \"FullyQualifiedName~Quadratic_|FullyQualifiedName~Live_Stmt\"",
    WorkingDirectory = work,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};
using var p = System.Diagnostics.Process.Start(psi)!;
Console.WriteLine(await p.StandardOutput.ReadToEndAsync());
await p.WaitForExitAsync();
if (p.ExitCode != 0) Environment.Exit(7);

Console.WriteLine("OK");
