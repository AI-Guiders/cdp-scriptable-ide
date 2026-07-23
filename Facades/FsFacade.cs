namespace Cdp.ScriptableIde;

public sealed class FsFacade(ScriptToolBus bus, PlanContext plan)
{
    public async Task<string> ReadTextAsync(string path, CancellationToken ct = default)
    {
        var resolved = plan.Resolve(path);
        var args = ScriptArgs.From(new { path = resolved });
        if (bus.IsDryRun)
        {
            bus.RecordLocal("fs", "read_text", args, StepResponse.Success("fs.read_text", "dry_run", new { dry_run = true, path = resolved }).ToJson(), skippedDryRun: true);
            return "";
        }

        if (!File.Exists(resolved))
            throw new FileNotFoundException("Read source missing.", resolved);
        var content = await File.ReadAllTextAsync(resolved, ct).ConfigureAwait(false);
        // Return raw text to CSX; step log keeps size only (avoid huge transcript).
        bus.RecordLocal("fs", "read_text", args,
            StepResponse.Success("fs.read_text", "read", new { path = resolved, bytes = content.Length }).ToJson());
        return content;
    }

    public async Task<StepResponse> WriteTextAsync(string path, string content, CancellationToken ct = default)
    {
        var resolved = plan.Resolve(path);
        var args = ScriptArgs.From(new { path = resolved, bytes = content.Length });
        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success("fs.write_text", "dry_run", new { dry_run = true, path = resolved });
            bus.RecordLocal("fs", "write_text", args, dry.ToJson(), skippedDryRun: true);
            return dry;
        }

        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(resolved, content, ct).ConfigureAwait(false);
        var result = StepResponse.Success("fs.write_text", "written", new { path = resolved });
        bus.RecordLocal("fs", "write_text", args, result.ToJson());
        return result;
    }

    /// <summary>Exact text replace (StrReplace-shaped). Fails if oldText missing or not unique when requireUnique.</summary>
    public async Task<StepResponse> ReplaceTextAsync(
        string path,
        string oldText,
        string newText,
        bool requireUnique = true,
        CancellationToken ct = default)
    {
        var resolved = plan.Resolve(path);
        var args = ScriptArgs.From(new
        {
            path = resolved,
            old_bytes = oldText.Length,
            new_bytes = newText.Length,
            require_unique = requireUnique
        });
        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success("fs.replace_text", "dry_run", new { dry_run = true, path = resolved });
            bus.RecordLocal("fs", "replace_text", args, dry.ToJson(), skippedDryRun: true);
            return dry;
        }

        if (!File.Exists(resolved))
            throw new FileNotFoundException("Replace source missing.", resolved);
        var content = await File.ReadAllTextAsync(resolved, ct).ConfigureAwait(false);
        var count = 0;
        var idx = 0;
        while ((idx = content.IndexOf(oldText, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += Math.Max(oldText.Length, 1);
        }
        if (count == 0)
            throw new InvalidOperationException($"ReplaceText: oldText not found in {resolved}");
        if (requireUnique && count != 1)
            throw new InvalidOperationException($"ReplaceText: oldText matched {count} times (requireUnique); path={resolved}");

        var updated = content.Replace(oldText, newText, StringComparison.Ordinal);
        await File.WriteAllTextAsync(resolved, updated, ct).ConfigureAwait(false);
        var result = StepResponse.Success("fs.replace_text", "replaced", new { path = resolved, replacements = count });
        bus.RecordLocal("fs", "replace_text", args, result.ToJson());
        return result;
    }

    public Task<StepResponse> RelocateAsync(string from, string to, CancellationToken ct = default)
    {
        var src = plan.Resolve(from);
        var dst = plan.Resolve(to);
        var args = ScriptArgs.From(new { from = src, to = dst });
        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success("fs.relocate", "dry_run", new { dry_run = true, from = src, to = dst });
            bus.RecordLocal("fs", "relocate", args, dry.ToJson(), skippedDryRun: true);
            return Task.FromResult(dry);
        }

        if (!File.Exists(src))
            throw new FileNotFoundException("Relocate source missing.", src);
        var dir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (File.Exists(dst) || Directory.Exists(dst))
            throw new InvalidOperationException($"Relocate target exists: {dst}");
        File.Move(src, dst);
        var result = StepResponse.Success("fs.relocate", "moved", new { from = src, to = dst });
        bus.RecordLocal("fs", "relocate", args, result.ToJson());
        return Task.FromResult(result);
    }
}
