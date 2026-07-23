using Cdp.ScriptableIde;

var work = Path.Combine(Path.GetTempPath(), "stdio-smoke-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
var bus = new ScriptToolBus();
var plan = new PlanContext { PrimaryRoot = work, WorkRoot = work, PlanId = "smoke", Language = "csharp" };

// Simulate MCP: real Console.Out must stay clean; CSX prints go into report.ConsoleOut.
var realOut = new StringWriter();
var prev = Console.Out;
Console.SetOut(realOut);
try
{
    var report = await ScriptHost.RunAsync(
        """
        Console.WriteLine("CTOR {\"ok\":true}");
        Console.WriteLine("UNIT {\"ok\":true}");
        Console.WriteLine("TM {\"ok\":true}");
        return "done";
        """,
        bus, plan, "run");

    Console.SetOut(prev);
    var leaked = realOut.ToString();
    if (!string.IsNullOrEmpty(leaked))
    {
        Console.Error.WriteLine("LEAKED to Console.Out: " + leaked);
        Environment.Exit(1);
    }

    if (report.ConsoleOut is null
        || !report.ConsoleOut.Contains("CTOR", StringComparison.Ordinal)
        || !report.ConsoleOut.Contains("UNIT", StringComparison.Ordinal)
        || !report.ConsoleOut.Contains("TM", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("expected ConsoleOut capture, got: " + report.ConsoleOut);
        Environment.Exit(2);
    }

    if (!report.Ok || report.Result != "done")
    {
        Console.Error.WriteLine("report failed: " + report.Error);
        Environment.Exit(3);
    }

    Console.WriteLine("OK captured_len=" + report.ConsoleOut.Length);
}
finally
{
    Console.SetOut(prev);
}
