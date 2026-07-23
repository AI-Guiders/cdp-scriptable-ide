using Cdp.ScriptableIde;

var work = Path.Combine(Path.GetTempPath(), "cdp-produce-smoke-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
var bus = new ScriptToolBus();
var plan = new PlanContext
{
    PrimaryRoot = work,
    WorkRoot = work,
    Language = "csharp"
};
var g = new ScriptGlobals(bus, plan);

var path = Path.Combine(work, "ProduceDemo.cs");
var rec = await g.Create.Record("UpsertResult")
    .With(Field.Named("ok").Of(Types.Boolean))
    .With(Field.Named("id").Of(Types.Of("Guid")))
    .Namespace("Demo")
    .Into(path)
    .Replace()
    .ApplyAsync();
Console.WriteLine("RECORD " + rec.Ok + " " + rec.Summary);
if (!rec.Ok) Environment.Exit(1);

var cls = await g.Create.Class("Host")
    .Public()
    .Namespace("Demo")
    .Into(path)
    .ApplyAsync();
Console.WriteLine("CLASS " + cls.Ok + " " + cls.Summary);
if (!cls.Ok) Environment.Exit(2);

var typeA = Anchor.File(path).Member("Host");
var fld = await g.Create.Field("_count").In(typeA).Of(Types.Integer).Private().ApplyAsync();
Console.WriteLine("FIELD " + fld.Ok + " " + fld.Summary);
if (!fld.Ok) Environment.Exit(3);

var conv = await g.Convert.ToProperty.At(Anchor.File(path).Member("_count")).ApplyAsync();
Console.WriteLine("CONV " + conv.Ok + " " + conv.Summary);
if (!conv.Ok) Environment.Exit(4);

var meth = await g.Create.Method("GetId")
    .In(typeA)
    .Public()
    .Returns(Types.Of("Guid"))
    .ApplyAsync();
Console.WriteLine("METH " + meth.Ok + " " + meth.Summary);
if (!meth.Ok) Environment.Exit(5);

var ch = await g.Refactor.Change
    .At(Anchor.File(path).Method("GetId").ReturnType())
    .To(Types.Of("UpsertResult"))
    .ApplyAsync();
Console.WriteLine("CHANGE " + ch.Ok + " " + ch.Summary);
if (!ch.Ok) Environment.Exit(6);

var text = File.ReadAllText(path);
if (!text.Contains("record UpsertResult", StringComparison.Ordinal)
    || !text.Contains("UpsertResult GetId", StringComparison.Ordinal)
    || !text.Contains("Count { get; set; }", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Unexpected file:\n" + text);
    Environment.Exit(7);
}

Console.WriteLine("OK\n" + text);
try { Directory.Delete(work, true); } catch { /* ignore */ }
