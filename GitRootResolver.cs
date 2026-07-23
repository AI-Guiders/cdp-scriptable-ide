namespace Cdp.ScriptableIde;

/// <summary>Resolve owning git root + plan scope for monorepo/submodule dogfood.</summary>
public static class GitRootResolver
{
    /// <summary>
    /// <paramref name="entryPath"/> = workspace_path or session project root (file or dir).
    /// Uses <c>git rev-parse --show-toplevel</c> so submodule paths resolve to the submodule, not the parent.
    /// </summary>
    public static string ResolveGitRoot(string entryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPath);
        var full = Path.GetFullPath(entryPath);
        var start = File.Exists(full) ? Path.GetDirectoryName(full)! : full;
        if (!Directory.Exists(start))
            throw new ArgumentException($"Path does not exist: {entryPath}");

        try
        {
            var top = ProcessUtil.RunGit(start, ["rev-parse", "--show-toplevel"]);
            // RunGit may append stderr; take first line
            var line = top.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                       ?? throw new ArgumentException($"Not a git repository: {entryPath}");
            return Path.GetFullPath(line.Trim());
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new ArgumentException($"Not a git repository: {entryPath}", ex);
        }
    }

    /// <summary>
    /// Relative scope under git root (forward slashes). Empty = whole repo.
    /// <paramref name="focusPath"/> defaults to entry (project dir after cdp_open).
    /// </summary>
    public static string ResolvePlanScope(string gitRoot, string? focusPath)
    {
        gitRoot = Path.GetFullPath(gitRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(focusPath))
            return "";

        var focus = Path.GetFullPath(focusPath);
        if (File.Exists(focus))
            focus = Path.GetDirectoryName(focus)!;

        focus = focus.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(focus, gitRoot, StringComparison.OrdinalIgnoreCase))
            return "";

        if (!focus.StartsWith(gitRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !focus.StartsWith(gitRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            // Focus outside git root — treat as whole-repo scope
            return "";
        }

        var rel = Path.GetRelativePath(gitRoot, focus);
        if (rel is "." or "")
            return "";
        return rel.Replace('\\', '/');
    }

    /// <summary>
    /// Fail-fast when primary has a populated scope but worktree scope is missing/empty
    /// (typical: wrong parent monorepo worktree with unpopulated submodule).
    /// </summary>
    public static void EnsureScopePopulated(string primaryRoot, string workRoot, string planScope)
    {
        if (string.IsNullOrEmpty(planScope))
            return;

        var primaryScope = Path.Combine(primaryRoot, planScope.Replace('/', Path.DirectorySeparatorChar));
        var workScope = Path.Combine(workRoot, planScope.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(primaryScope))
            return;

        var primaryFiles = CountFiles(primaryScope);
        if (primaryFiles == 0)
            return;

        if (!Directory.Exists(workScope) || CountFiles(workScope) == 0)
        {
            var submoduleHint = LooksLikeSubmoduleGap(primaryRoot, planScope)
                ? " Path looks like a submodule gap — pass owning repo root (git rev-parse --show-toplevel from the project), not the parent monorepo."
                : "";
            throw new InvalidOperationException(
                $"PlanScope '{planScope}' is populated on primary ({primaryFiles} files) but missing/empty in worktree.{submoduleHint} " +
                "Wrong GitRoot or unpopulated submodule checkout.");
        }
    }

    private static bool LooksLikeSubmoduleGap(string gitRoot, string planScope)
    {
        var modules = Path.Combine(gitRoot, ".gitmodules");
        if (!File.Exists(modules))
            return true; // parent often has modules; missing scope still suspicious
        var text = File.ReadAllText(modules);
        var firstSeg = planScope.Split('/')[0];
        return text.Contains($"path = {firstSeg}", StringComparison.Ordinal)
               || text.Contains($"path = {planScope}", StringComparison.Ordinal)
               || planScope.Contains('/', StringComparison.Ordinal);
    }

    private static int CountFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Take(20).Count();
        }
        catch
        {
            return 0;
        }
    }
}
