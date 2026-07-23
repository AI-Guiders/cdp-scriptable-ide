namespace Cdp.ScriptableIde;

/// <summary>Execution root for a plan. In worktree mode WorkRoot ≠ PrimaryRoot. Open.* may rebind roots mid-script.</summary>
public sealed class PlanContext
{
    public required string PrimaryRoot { get; set; }
    public required string WorkRoot { get; set; }
    public string PlanId { get; set; } = "";
    /// <summary>Optional csharp anchor from CDP session after cdp_open / Open.At.</summary>
    public string? SolutionOrProjectPath { get; set; }
    public string? Language { get; set; }

    /// <summary>Project conventions — hydrate via <see cref="ProjectSettingsLoader.Hydrate"/>.</summary>
    public ProjectSettings Settings { get; init; } = new();

    public bool IsWorktree => !string.Equals(
        Path.GetFullPath(PrimaryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(WorkRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);

    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return WorkRoot;
        var full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(WorkRoot, path));
        var primary = Path.GetFullPath(PrimaryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.StartsWith(primary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, primary, StringComparison.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(primary, full);
            return Path.GetFullPath(Path.Combine(WorkRoot, rel));
        }
        return full;
    }

    /// <summary>Rebind session roots (Open Recent / Anchor→solution). Keeps PlanId.</summary>
    public void Rebind(string root, string? solutionOrProjectPath, string? language = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var full = Path.GetFullPath(root);
        PrimaryRoot = full;
        WorkRoot = full;
        SolutionOrProjectPath = string.IsNullOrWhiteSpace(solutionOrProjectPath)
            ? null
            : Path.GetFullPath(solutionOrProjectPath);
        if (!string.IsNullOrWhiteSpace(language))
            Language = language;
        ProjectSettingsLoader.Hydrate(this);
    }
}
