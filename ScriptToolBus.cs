using System.Text.Json;

namespace Cdp.ScriptableIde;

public sealed class ScriptToolBus(Func<string, string, IReadOnlyDictionary<string, JsonElement>, CancellationToken, Task<string>>? invoke = null) : IScriptToolBus
{
    private readonly List<ScriptStep> _steps = [];
    private readonly List<string> _scratchDirs = [];
    private readonly Func<string, string, IReadOnlyDictionary<string, JsonElement>, CancellationToken, Task<string>> _invoke =
        invoke ?? ((_, _, _, _) => throw new InvalidOperationException("No tool dispatcher configured."));

    public bool IsDryRun { get; init; }
    public IReadOnlyList<ScriptStep> Steps => _steps;
    public IReadOnlyList<string> ScratchDirs => _scratchDirs;

    public void RegisterScratch(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var full = Path.GetFullPath(path);
        if (!_scratchDirs.Contains(full, StringComparer.OrdinalIgnoreCase))
            _scratchDirs.Add(full);
    }

    /// <summary>Best-effort delete of registered TEMP scratches (never touches WorkRoot).</summary>
    public IReadOnlyList<string> CleanupScratches()
    {
        var removed = new List<string>();
        var tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var dir in _scratchDirs.ToArray())
        {
            try
            {
                var full = Path.GetFullPath(dir);
                if (!full.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(full, tempRoot, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!Directory.Exists(full))
                    continue;
                Directory.Delete(full, recursive: true);
                removed.Add(full);
            }
            catch
            {
                // best-effort
            }
        }

        _scratchDirs.Clear();
        return removed;
    }

    public void RecordLocal(
        string domain,
        string underlying,
        IReadOnlyDictionary<string, JsonElement> args,
        string? result,
        bool skippedDryRun = false)
    {
        _steps.Add(new ScriptStep(domain, underlying, args, result, skippedDryRun, DateTimeOffset.UtcNow));
    }

    public async Task<string> InvokeAsync(
        string domain,
        string underlying,
        IReadOnlyDictionary<string, JsonElement> args,
        CancellationToken cancellationToken = default)
    {
        if (IsDryRun)
        {
            RecordLocal(domain, underlying, args, null, skippedDryRun: true);
            return StepResponse.Success("script.dry_run", "skipped", new { dry_run = true, skipped = true }).ToJson();
        }

        var result = await _invoke(domain, underlying, args, cancellationToken).ConfigureAwait(false);
        RecordLocal(domain, underlying, args, result, skippedDryRun: false);
        return result;
    }
}

internal static class ScriptArgs
{
    public static Dictionary<string, JsonElement> From(object anon)
    {
        var el = JsonSerializer.SerializeToElement(anon);
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in el.EnumerateObject())
            dict[p.Name] = p.Value.Clone();
        return dict;
    }
}
