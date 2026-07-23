using System.Security.Cryptography;
using System.Text.Json;

namespace Cdp.ScriptableIde;

/// <summary>Remap path-like JSON args from primary root into worktree root.</summary>
public sealed class PathRemappingToolBus(IScriptToolBus inner, PlanContext plan) : IScriptToolBus
{
    private static readonly string[] PathKeys =
    [
        "workspace_path", "solution_or_project_path", "solution_path", "file_path",
        "target_path", "path", "cwd", "project_path", "output_file_path"
    ];

    public bool IsDryRun => inner.IsDryRun;
    public IReadOnlyList<ScriptStep> Steps => inner.Steps;

    public void RecordLocal(
        string domain,
        string underlying,
        IReadOnlyDictionary<string, JsonElement> args,
        string? result,
        bool skippedDryRun = false) =>
        inner.RecordLocal(domain, underlying, args, result, skippedDryRun);

    public Task<string> InvokeAsync(
        string domain,
        string underlying,
        IReadOnlyDictionary<string, JsonElement> args,
        CancellationToken cancellationToken = default)
    {
        var mapped = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (k, v) in args)
        {
            if (v.ValueKind == JsonValueKind.String
                && PathKeys.Contains(k, StringComparer.Ordinal)
                && v.GetString() is { Length: > 0 } s)
            {
                mapped[k] = JsonSerializer.SerializeToElement(plan.Resolve(s));
            }
            else
                mapped[k] = v;
        }
        return inner.InvokeAsync(domain, underlying, mapped, cancellationToken);
    }
}

public static class WorktreePlanRunner
{
    public const string PromoteOverlapSafe = "overlap_safe";
    public const string PromoteStrictClean = "strict_clean";

    private static readonly Dictionary<string, ActivePlan> Plans = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public sealed record ActivePlan(
        string PlanId,
        string PrimaryRoot,
        string WorkRoot,
        string BranchName,
        string PlanScope,
        string BaseTreeSha,
        IReadOnlyDictionary<string, string> BaseFileHashes,
        int OverlayPathCount,
        string PromotePolicy,
        ScriptReport LastReport);

    public static IReadOnlyCollection<string> ListPlanIds()
    {
        lock (Gate) return Plans.Keys.ToArray();
    }

    public static async Task<ScriptReport> RunInWorktreeAsync(
        string code,
        string entryPath,
        Func<string, string, IReadOnlyDictionary<string, JsonElement>, CancellationToken, Task<string>> invoke,
        CancellationToken cancellationToken = default,
        string? focusPath = null,
        string promotePolicy = PromoteOverlapSafe)
    {
        var check = await ScriptHost.CheckAsync(code, cancellationToken).ConfigureAwait(false);
        if (!check.Ok)
            return check with { Mode = "run_plan_worktree" };

        if (promotePolicy is not (PromoteOverlapSafe or PromoteStrictClean))
            promotePolicy = PromoteOverlapSafe;

        string gitRoot;
        string planScope;
        try
        {
            gitRoot = GitRootResolver.ResolveGitRoot(entryPath);
            planScope = GitRootResolver.ResolvePlanScope(gitRoot, focusPath ?? entryPath);
        }
        catch (Exception ex)
        {
            return new ScriptReport
            {
                Ok = false,
                Mode = "run_plan_worktree",
                Error = ex.Message,
                PrimaryRoot = entryPath
            };
        }

        var planId = $"plan-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        var workRoot = Path.Combine(Path.GetTempPath(), "cdp-csx-worktrees", planId);
        var branch = $"cdp-csx/{planId}";

        Directory.CreateDirectory(Path.GetDirectoryName(workRoot)!);
        try
        {
            RunGit(gitRoot, ["worktree", "add", "-b", branch, workRoot, "HEAD"]);
        }
        catch (Exception ex)
        {
            return new ScriptReport
            {
                Ok = false,
                Mode = "run_plan_worktree",
                Error = $"git worktree add failed: {ex.Message}",
                PlanId = planId,
                PrimaryRoot = gitRoot,
                WorkRoot = workRoot,
                GitRoot = gitRoot,
                PlanScope = planScope
            };
        }

        try
        {
            GitRootResolver.EnsureScopePopulated(gitRoot, workRoot, planScope);
        }
        catch (Exception ex)
        {
            TryCleanupWorktree(gitRoot, workRoot, branch);
            return new ScriptReport
            {
                Ok = false,
                Mode = "run_plan_worktree",
                Error = ex.Message,
                PlanId = planId,
                PrimaryRoot = gitRoot,
                WorkRoot = workRoot,
                GitRoot = gitRoot,
                PlanScope = planScope
            };
        }

        int overlayCount;
        Dictionary<string, string> baseHashes;
        string baseTreeSha;
        try
        {
            (overlayCount, baseHashes) = OverlayPrimaryWorkingTree(gitRoot, workRoot, planScope);
            baseTreeSha = SnapshotBaseTree(workRoot, planScope);
        }
        catch (Exception ex)
        {
            TryCleanupWorktree(gitRoot, workRoot, branch);
            return new ScriptReport
            {
                Ok = false,
                Mode = "run_plan_worktree",
                Error = $"overlay/base snapshot failed: {ex.Message}",
                PlanId = planId,
                PrimaryRoot = gitRoot,
                WorkRoot = workRoot,
                GitRoot = gitRoot,
                PlanScope = planScope
            };
        }

        var plan = new PlanContext
        {
            PlanId = planId,
            PrimaryRoot = gitRoot,
            WorkRoot = workRoot
        };
        ProjectSettingsLoader.Hydrate(plan);

        var remapBus = new ScriptToolBus(async (d, u, a, ct) =>
        {
            var mapped = RemapArgs(a, plan);
            return await invoke(d, u, mapped, ct).ConfigureAwait(false);
        })
        { IsDryRun = false };

        var report = await ScriptHost.RunAsync(code, remapBus, plan, "run_plan_worktree", cancellationToken)
            .ConfigureAwait(false);

        string? status = null;
        string? diff = null;
        try
        {
            status = RunGit(workRoot, ["status", "--porcelain"]);
            // Plan delta vs base tree (not full WIP overlay)
            diff = RunGitSafe(workRoot, ["diff", "--no-ext-diff", baseTreeSha]);
        }
        catch
        {
            // ignore status failures
        }

        var primaryStatus = RunGitSafe(gitRoot, ["status", "--porcelain"]);
        var enriched = new ScriptReport
        {
            Ok = report.Ok,
            Mode = "run_plan_worktree",
            Result = report.Result,
            Error = report.Error,
            Diagnostics = report.Diagnostics,
            Steps = report.Steps,
            PlanId = planId,
            PrimaryRoot = gitRoot,
            WorkRoot = workRoot,
            WorktreeStatus = status,
            WorktreeDiff = diff is { Length: > 8000 } ? diff[..8000] + "…" : diff,
            PrimaryClean = string.IsNullOrWhiteSpace(primaryStatus),
            GitRoot = gitRoot,
            PlanScope = planScope,
            OverlayPathCount = overlayCount,
            PromotePolicy = promotePolicy,
            BaseTreeSha = baseTreeSha
        };

        lock (Gate)
        {
            Plans[planId] = new ActivePlan(
                planId, gitRoot, workRoot, branch, planScope, baseTreeSha, baseHashes,
                overlayCount, promotePolicy, enriched);
        }

        return enriched;
    }

    public static ScriptReport Discard(string planId)
    {
        ActivePlan plan;
        lock (Gate)
        {
            if (!Plans.TryGetValue(planId, out plan!))
                return new ScriptReport { Ok = false, Mode = "discard", Error = $"Unknown plan_id: {planId}", PlanId = planId };
        }

        try
        {
            RunGit(plan.PrimaryRoot, ["worktree", "remove", "--force", plan.WorkRoot]);
        }
        catch (Exception ex)
        {
            TryDeleteDir(plan.WorkRoot);
            return new ScriptReport
            {
                Ok = false,
                Mode = "discard",
                Error = $"worktree remove failed (cleaned dir best-effort): {ex.Message}",
                PlanId = planId,
                PrimaryRoot = plan.PrimaryRoot,
                WorkRoot = plan.WorkRoot,
                GitRoot = plan.PrimaryRoot,
                PlanScope = plan.PlanScope
            };
        }

        try { RunGit(plan.PrimaryRoot, ["branch", "-D", plan.BranchName]); }
        catch { /* branch may already be gone */ }

        lock (Gate) Plans.Remove(planId);
        return new ScriptReport
        {
            Ok = true,
            Mode = "discard",
            Result = "worktree removed; primary untouched",
            PlanId = planId,
            PrimaryRoot = plan.PrimaryRoot,
            WorkRoot = plan.WorkRoot,
            PrimaryClean = string.IsNullOrWhiteSpace(RunGitSafe(plan.PrimaryRoot, ["status", "--porcelain"])),
            GitRoot = plan.PrimaryRoot,
            PlanScope = plan.PlanScope,
            OverlayPathCount = plan.OverlayPathCount,
            PromotePolicy = plan.PromotePolicy,
            BaseTreeSha = plan.BaseTreeSha
        };
    }

    public static ScriptReport Promote(string planId, string? promotePolicy = null)
    {
        ActivePlan plan;
        lock (Gate)
        {
            if (!Plans.TryGetValue(planId, out plan!))
                return new ScriptReport { Ok = false, Mode = "promote", Error = $"Unknown plan_id: {planId}", PlanId = planId };
        }

        var policy = promotePolicy is PromoteOverlapSafe or PromoteStrictClean
            ? promotePolicy
            : plan.PromotePolicy;

        try
        {
            var primaryStatus = RunGitSafe(plan.PrimaryRoot, ["status", "--porcelain"]);
            if (policy == PromoteStrictClean && !string.IsNullOrWhiteSpace(primaryStatus))
            {
                var preview = primaryStatus.Length > 600 ? primaryStatus[..600] + "…" : primaryStatus;
                return new ScriptReport
                {
                    Ok = false,
                    Mode = "promote",
                    Error =
                        "promote refused (strict_clean): primary working tree is dirty. " +
                        "Commit/stash first, or cdp_csx_discard. Status:\n" + preview,
                    PlanId = planId,
                    PrimaryRoot = plan.PrimaryRoot,
                    WorkRoot = plan.WorkRoot,
                    PrimaryClean = false,
                    GitRoot = plan.PrimaryRoot,
                    PlanScope = plan.PlanScope,
                    PromotePolicy = policy,
                    OverlayPathCount = plan.OverlayPathCount,
                    BaseTreeSha = plan.BaseTreeSha
                };
            }

            StageScope(plan.WorkRoot, plan.PlanScope);
            var newTree = FirstLine(RunGit(plan.WorkRoot, ["write-tree"]));
            var nameStatus = RunGitSafe(plan.WorkRoot, ["diff", "--name-status", plan.BaseTreeSha, newTree]);
            var changes = ParseNameStatus(nameStatus);

            if (changes.Count == 0)
            {
                var emptyDiscard = Discard(planId);
                return new ScriptReport
                {
                    Ok = emptyDiscard.Ok,
                    Mode = "promote",
                    Result = emptyDiscard.Ok
                        ? "nothing to promote (empty plan delta); worktree removed"
                        : emptyDiscard.Error,
                    Error = emptyDiscard.Ok ? null : emptyDiscard.Error,
                    PlanId = planId,
                    PrimaryRoot = plan.PrimaryRoot,
                    WorkRoot = plan.WorkRoot,
                    PrimaryClean = emptyDiscard.PrimaryClean,
                    GitRoot = plan.PrimaryRoot,
                    PlanScope = plan.PlanScope,
                    PromotePolicy = policy,
                    OverlayPathCount = plan.OverlayPathCount,
                    BaseTreeSha = plan.BaseTreeSha
                };
            }

            var patchPaths = changes.Select(c => c.Path).ToList();

            if (policy == PromoteOverlapSafe)
            {
                var dirtyPrimary = ParsePorcelainPaths(primaryStatus);
                var conflicts = new List<string>();
                foreach (var path in patchPaths)
                {
                    if (!dirtyPrimary.Contains(path))
                        continue;

                    var primaryFile = Path.Combine(plan.PrimaryRoot, path.Replace('/', Path.DirectorySeparatorChar));
                    var baseBlob = TryBlobAtTree(plan.WorkRoot, plan.BaseTreeSha, path);
                    var primaryBlob = File.Exists(primaryFile)
                        ? FirstLine(RunGitSafe(plan.PrimaryRoot, ["hash-object", primaryFile]))
                        : null;

                    if (baseBlob is null)
                    {
                        if (primaryBlob is { Length: > 0 })
                            conflicts.Add(path);
                    }
                    else if (!string.Equals(baseBlob, primaryBlob, StringComparison.OrdinalIgnoreCase))
                    {
                        conflicts.Add(path);
                    }
                }

                if (conflicts.Count > 0)
                {
                    return new ScriptReport
                    {
                        Ok = false,
                        Mode = "promote",
                        Error =
                            "promote refused (overlap_safe): primary changed under plan paths after plan start " +
                            "(worktree kept). Conflicts:\n" + string.Join("\n", conflicts),
                        PlanId = planId,
                        PrimaryRoot = plan.PrimaryRoot,
                        WorkRoot = plan.WorkRoot,
                        PrimaryClean = false,
                        GitRoot = plan.PrimaryRoot,
                        PlanScope = plan.PlanScope,
                        PromotePolicy = policy,
                        OverlayPathCount = plan.OverlayPathCount,
                        BaseTreeSha = plan.BaseTreeSha
                    };
                }
            }

            try
            {
                ApplyPlanDeltaFiles(plan.PrimaryRoot, plan.WorkRoot, changes);
            }
            catch (Exception ex)
            {
                return new ScriptReport
                {
                    Ok = false,
                    Mode = "promote",
                    Error =
                        "promote refused: applying plan delta files failed (worktree kept). " + ex.Message,
                    PlanId = planId,
                    PrimaryRoot = plan.PrimaryRoot,
                    WorkRoot = plan.WorkRoot,
                    PrimaryClean = string.IsNullOrWhiteSpace(primaryStatus),
                    GitRoot = plan.PrimaryRoot,
                    PlanScope = plan.PlanScope,
                    PromotePolicy = policy,
                    OverlayPathCount = plan.OverlayPathCount,
                    BaseTreeSha = plan.BaseTreeSha
                };
            }

            var discard = Discard(planId);
            return new ScriptReport
            {
                Ok = discard.Ok,
                Mode = "promote",
                Result = discard.Ok ? "plan delta applied to primary; worktree removed" : discard.Error,
                Error = discard.Ok ? null : discard.Error,
                PlanId = planId,
                PrimaryRoot = plan.PrimaryRoot,
                WorkRoot = plan.WorkRoot,
                PrimaryClean = discard.PrimaryClean,
                GitRoot = plan.PrimaryRoot,
                PlanScope = plan.PlanScope,
                PromotePolicy = policy,
                OverlayPathCount = plan.OverlayPathCount,
                BaseTreeSha = plan.BaseTreeSha
            };
        }
        catch (Exception ex)
        {
            return new ScriptReport
            {
                Ok = false,
                Mode = "promote",
                Error = ex.Message,
                PlanId = planId,
                PrimaryRoot = plan.PrimaryRoot,
                WorkRoot = plan.WorkRoot,
                GitRoot = plan.PrimaryRoot,
                PlanScope = plan.PlanScope,
                PromotePolicy = policy
            };
        }
    }

    /// <summary>Copy primary dirty/untracked files under scope into worktree; hash for promote conflicts.</summary>
    private static (int Count, Dictionary<string, string> Hashes) OverlayPrimaryWorkingTree(
        string primaryRoot, string workRoot, string planScope)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var porcelainArgs = new List<string> { "status", "--porcelain", "-u" };
        if (!string.IsNullOrEmpty(planScope))
        {
            porcelainArgs.Add("--");
            porcelainArgs.Add(planScope);
        }

        var porcelain = RunGitSafe(primaryRoot, porcelainArgs);
        var paths = ParsePorcelainPaths(porcelain);
        var count = 0;
        foreach (var rel in paths)
        {
            if (!string.IsNullOrEmpty(planScope)
                && !rel.StartsWith(planScope + "/", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rel, planScope, StringComparison.OrdinalIgnoreCase))
                continue;

            var src = Path.Combine(primaryRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var dst = Path.Combine(workRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src))
                continue;
            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.Copy(src, dst, overwrite: true);
            hashes[rel.Replace('\\', '/')] = HashFile(src);
            count++;
        }

        // Also hash clean tracked files under scope that exist in both trees (for later conflict if primary dirties)
        // — not required for overlay; conflict check uses BaseFileHashes only for overlaid paths.
        // For patch paths that were clean at start, BaseFileHashes may miss them: treat missing as
        // "must not be dirty on primary" (already handled: dirty ∩ patch without hash → conflict).
        return (count, hashes);
    }

    private static string SnapshotBaseTree(string workRoot, string planScope)
    {
        StageScope(workRoot, planScope);
        return FirstLine(RunGit(workRoot, ["write-tree"]));
    }

    private static void StageScope(string workRoot, string planScope)
    {
        if (string.IsNullOrEmpty(planScope))
            RunGit(workRoot, ["add", "-A"]);
        else
            RunGit(workRoot, ["add", "-A", "--", planScope]);
    }

    private static HashSet<string> ParsePorcelainPaths(string? porcelain)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(porcelain))
            return set;
        foreach (var raw in porcelain.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw;
            if (line.Length < 4)
                continue;
            // "XY PATH" or "XY ORIG -> PATH"
            var pathPart = line[3..];
            if (pathPart.Contains(" -> ", StringComparison.Ordinal))
                pathPart = pathPart.Split(" -> ", 2, StringSplitOptions.None)[1];
            pathPart = pathPart.Trim().Trim('"').Replace('\\', '/');
            if (pathPart.Length > 0)
                set.Add(pathPart);
        }
        return set;
    }

    private static List<(char Status, string Path)> ParseNameStatus(string? text)
    {
        var list = new List<(char, string)>();
        if (string.IsNullOrWhiteSpace(text))
            return list;
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd();
            if (line.Length < 3)
                continue;
            var status = line[0];
            // "A\tpath" or "R100\told\tnew" — take last tab field as path
            var parts = line.Split('\t');
            if (parts.Length < 2)
                continue;
            var path = parts[^1].Trim().Replace('\\', '/');
            if (path.Length > 0)
                list.Add((status, path));
        }
        return list;
    }

    /// <summary>Copy/delete plan-delta paths from worktree onto primary (avoids fragile git-apply patches).</summary>
    private static void ApplyPlanDeltaFiles(
        string primaryRoot,
        string workRoot,
        IReadOnlyList<(char Status, string Path)> changes)
    {
        var toStage = new List<string>();
        foreach (var (status, rel) in changes)
        {
            var primaryPath = Path.Combine(primaryRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var workPath = Path.Combine(workRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (status == 'D')
            {
                if (File.Exists(primaryPath))
                    File.Delete(primaryPath);
                toStage.Add(rel);
                continue;
            }

            if (!File.Exists(workPath))
                throw new InvalidOperationException($"Worktree missing plan file: {rel}");
            var dir = Path.GetDirectoryName(primaryPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.Copy(workPath, primaryPath, overwrite: true);
            toStage.Add(rel);
        }

        if (toStage.Count > 0)
        {
            var args = new List<string> { "add", "--" };
            args.AddRange(toStage);
            RunGit(primaryRoot, args);
        }
    }

    private static string? TryBlobAtTree(string repo, string treeSha, string path)
    {
        try
        {
            var blob = FirstLine(RunGit(repo, ["rev-parse", $"{treeSha}:{path.Replace('\\', '/')}"]));
            return string.IsNullOrWhiteSpace(blob) ? null : blob;
        }
        catch
        {
            return null;
        }
    }

    private static string HashFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string FirstLine(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
        ?? text.Trim();

    private static void TryCleanupWorktree(string gitRoot, string workRoot, string branch)
    {
        try { RunGit(gitRoot, ["worktree", "remove", "--force", workRoot]); }
        catch { TryDeleteDir(workRoot); }
        try { RunGit(gitRoot, ["branch", "-D", branch]); }
        catch { /* ignore */ }
    }

    private static Dictionary<string, JsonElement> RemapArgs(
        IReadOnlyDictionary<string, JsonElement> args, PlanContext plan)
    {
        string[] keys =
        [
            "workspace_path", "solution_or_project_path", "solution_path", "file_path",
            "target_path", "path", "cwd", "project_path", "output_file_path"
        ];
        var mapped = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (k, v) in args)
        {
            if (v.ValueKind == JsonValueKind.String
                && keys.Contains(k, StringComparer.Ordinal)
                && v.GetString() is { Length: > 0 } s)
                mapped[k] = JsonSerializer.SerializeToElement(plan.Resolve(s));
            else
                mapped[k] = v;
        }
        return mapped;
    }

    private static string RunGit(string cwd, IReadOnlyList<string> args) => ProcessUtil.RunGit(cwd, args);

    private static string RunGitSafe(string cwd, IReadOnlyList<string> args)
    {
        try { return ProcessUtil.RunGit(cwd, args); }
        catch { return ""; }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* ignore */ }
    }
}
