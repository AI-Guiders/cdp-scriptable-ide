using System.Diagnostics;
using System.Text;

namespace Cdp.ScriptableIde;

internal static class ProcessUtil
{
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? env,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);
        if (env is not null)
        {
            foreach (var (k, v) in env)
                psi.Environment[k] = v;
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start: {fileName}");
        p.StandardInput.Close();
        var stdoutTask = p.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = p.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (p.ExitCode, stdout, stderr);
    }

    public static string RunGit(string cwd, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_PAGER"] = "cat";
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        p.StandardInput.Close();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException($"git {string.Join(' ', args)} timed out after 60s");
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} → {p.ExitCode}: {stdout}{stderr}".Trim());
        // Never append stderr to successful stdout — binary patches / SHAs must stay clean.
        return stdout.TrimEnd('\r', '\n');
    }
}
