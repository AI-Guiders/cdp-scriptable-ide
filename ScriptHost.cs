using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Cdp.ScriptableIde;

public static class ScriptHost
{
    /// <summary>Serialize Console.Out redirect — MCP stdio process must not see CSX prints.</summary>
    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    private static readonly ScriptOptions DefaultOptions = ScriptOptions.Default
        .AddReferences(typeof(ScriptGlobals).Assembly)
        .AddImports(
            "System",
            "System.IO",
            "System.Threading",
            "System.Threading.Tasks",
            "Cdp.ScriptableIde");

    public static async Task<ScriptReport> CheckAsync(string code, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        try
        {
            var script = CSharpScript.Create(code, DefaultOptions, typeof(ScriptGlobals));
            var diags = script.Compile();
            var items = CsxDiagnosticProjection.FromDiagnostics(diags);
            var legacy = CsxDiagnosticProjection.ToLegacyStrings(items);
            return new ScriptReport
            {
                Ok = items.Count == 0,
                Mode = "check",
                Diagnostics = legacy,
                DiagnosticItems = items,
                Evidence = Cdp.Evidence.EvidencePreprocess.ToDto(CsxDiagnosticProjection.ToEvidence(items)),
                Error = items.Count == 0 ? null : string.Join("\n", legacy)
            };
        }
        catch (Exception ex)
        {
            return new ScriptReport { Ok = false, Mode = "check", Error = ex.Message, Diagnostics = [ex.Message] };
        }
    }

    public static Task<ScriptReport> RunAsync(
        string code,
        ScriptToolBus bus,
        string mode = "run",
        CancellationToken cancellationToken = default)
    {
        var plan = new PlanContext
        {
            PrimaryRoot = Environment.CurrentDirectory,
            WorkRoot = Environment.CurrentDirectory,
            PlanId = ""
        };
        return RunAsync(code, bus, plan, mode, cancellationToken);
    }

    public static async Task<ScriptReport> RunAsync(
        string code,
        ScriptToolBus bus,
        PlanContext plan,
        string mode = "run",
        CancellationToken cancellationToken = default)
    {
        ProjectSettingsLoader.Hydrate(plan);
        var globals = new ScriptGlobals(bus, plan);

        await ConsoleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var captured = new StringBuilder();
        var writer = new StringWriter(captured);
        var prevOut = Console.Out;
        Console.SetOut(writer);
        try
        {
            return await RunCoreAsync(code, bus, plan, mode, globals, captured, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(prevOut);
            writer.Dispose();
            ConsoleGate.Release();
        }
    }

    private static async Task<ScriptReport> RunCoreAsync(
        string code,
        ScriptToolBus bus,
        PlanContext plan,
        string mode,
        ScriptGlobals globals,
        StringBuilder captured,
        CancellationToken cancellationToken)
    {
        try
        {
            var state = await CSharpScript.RunAsync(code, DefaultOptions, globals, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var result = state.ReturnValue?.ToString();
            return new ScriptReport
            {
                Ok = true,
                Mode = mode,
                Result = result,
                Steps = bus.Steps.ToArray(),
                PlanId = plan.PlanId,
                PrimaryRoot = plan.PrimaryRoot,
                WorkRoot = plan.WorkRoot,
                ConsoleOut = CapturedOrNull(captured)
            };
        }
        catch (CompilationErrorException cex)
        {
            var items = CsxDiagnosticProjection.FromDiagnostics(cex.Diagnostics);
            var legacy = CsxDiagnosticProjection.ToLegacyStrings(items);
            return new ScriptReport
            {
                Ok = false,
                Mode = mode,
                Error = string.Join("\n", legacy),
                Diagnostics = legacy,
                DiagnosticItems = items,
                Evidence = Cdp.Evidence.EvidencePreprocess.ToDto(CsxDiagnosticProjection.ToEvidence(items)),
                Steps = bus.Steps.ToArray(),
                PlanId = plan.PlanId,
                PrimaryRoot = plan.PrimaryRoot,
                WorkRoot = plan.WorkRoot,
                ConsoleOut = CapturedOrNull(captured)
            };
        }
        catch (Exception ex)
        {
            return new ScriptReport
            {
                Ok = false,
                Mode = mode,
                Error = ex.Message,
                Diagnostics = [ex.Message],
                Steps = bus.Steps.ToArray(),
                PlanId = plan.PlanId,
                PrimaryRoot = plan.PrimaryRoot,
                WorkRoot = plan.WorkRoot,
                ConsoleOut = CapturedOrNull(captured)
            };
        }
    }

    private static string? CapturedOrNull(StringBuilder sb)
    {
        var t = sb.ToString();
        return string.IsNullOrEmpty(t) ? null : t;
    }
}
