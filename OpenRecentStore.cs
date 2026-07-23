namespace Cdp.ScriptableIde;

/// <summary>Pluggable Open Recent persistence (CDP wires WitDB; smoke uses memory).</summary>
public interface IOpenRecentBackend
{
    void Push(string path, string? root, string? kind, string? language);
    IReadOnlyList<OpenRecentStore.Entry> List(int take);
    OpenRecentStore.Entry? TryGet(int zeroBasedIndex);
    /// <summary>Human-readable location (witdb path or "memory").</summary>
    string Location { get; }
}

/// <summary>
/// Open Recent façade — agent mirror of classic IDE + CIDE anchor→solution.
/// Host (cdp-mcp) configures WitDB backend; without host → in-memory only.
/// </summary>
public static class OpenRecentStore
{
    public const int DefaultCapacity = 20;

    public sealed record Entry(
        string Path,
        string? Root,
        string? Kind,
        string? Language,
        DateTimeOffset OpenedUtc);

    private static readonly object Gate = new();
    private static IOpenRecentBackend _backend = new MemoryOpenRecentBackend();

    public static string Location
    {
        get
        {
            lock (Gate)
                return _backend.Location;
        }
    }

    public static void Configure(IOpenRecentBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        lock (Gate)
            _backend = backend;
    }

    public static IReadOnlyList<Entry> List(int take = DefaultCapacity)
    {
        lock (Gate)
            return _backend.List(take);
    }

    public static Entry? TryGet(int zeroBasedIndex)
    {
        lock (Gate)
            return _backend.TryGet(zeroBasedIndex);
    }

    public static void Push(string path, string? root = null, string? kind = null, string? language = null)
    {
        lock (Gate)
            _backend.Push(path, root, kind, language);
    }
}

/// <summary>In-process fallback when CDP WitDB is not configured (unit/smoke).</summary>
public sealed class MemoryOpenRecentBackend : IOpenRecentBackend
{
    private readonly List<OpenRecentStore.Entry> _items = [];

    public string Location => "memory";

    public void Push(string path, string? root, string? kind, string? language)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var full = Path.GetFullPath(path.Trim());
        _items.RemoveAll(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, new OpenRecentStore.Entry(
            full,
            root is { Length: > 0 } r ? Path.GetFullPath(r) : Path.GetDirectoryName(full),
            kind,
            language,
            DateTimeOffset.UtcNow));
        if (_items.Count > OpenRecentStore.DefaultCapacity)
            _items.RemoveRange(OpenRecentStore.DefaultCapacity, _items.Count - OpenRecentStore.DefaultCapacity);
    }

    public IReadOnlyList<OpenRecentStore.Entry> List(int take)
    {
        var alive = _items.Where(e => File.Exists(e.Path) || Directory.Exists(e.Path)).ToList();
        if (alive.Count != _items.Count)
        {
            _items.Clear();
            _items.AddRange(alive);
        }

        if (take <= 0 || take >= alive.Count)
            return alive;
        return alive.Take(take).ToArray();
    }

    public OpenRecentStore.Entry? TryGet(int zeroBasedIndex)
    {
        var all = List(OpenRecentStore.DefaultCapacity);
        if (zeroBasedIndex < 0 || zeroBasedIndex >= all.Count)
            return null;
        return all[zeroBasedIndex];
    }
}

/// <summary>Walk up from a file/dir to nearest .sln/.slnx/.csproj (csharp) or tsconfig.json.</summary>
public static class ProjectBind
{
    public sealed record Result(string Root, string AnchorPath, string Kind, string Language);

    public static bool TryDetect(string pathOrFile, out Result result, out string error)
    {
        result = default!;
        error = "";
        if (string.IsNullOrWhiteSpace(pathOrFile))
        {
            error = "path_empty";
            return false;
        }

        string start;
        try
        {
            var full = Path.GetFullPath(pathOrFile.Trim());
            start = File.Exists(full)
                ? Path.GetDirectoryName(full)!
                : Directory.Exists(full)
                    ? full
                    : "";
            if (start.Length == 0)
            {
                error = "path_not_found";
                return false;
            }

            var name = Path.GetFileName(full);
            if (File.Exists(full))
            {
                if (name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    result = new Result(Path.GetDirectoryName(full)!, full, "csproj", "csharp");
                    return true;
                }

                if (name.Equals("tsconfig.json", StringComparison.OrdinalIgnoreCase))
                {
                    result = new Result(Path.GetDirectoryName(full)!, full, "tsconfig", "typescript");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        for (var cur = start; cur is not null; cur = Directory.GetParent(cur)?.FullName)
        {
            foreach (var pattern in new[] { "*.slnx", "*.sln", "*.csproj" })
            {
                var hits = Directory.EnumerateFiles(cur, pattern, SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToList();
                if (hits.Count == 0)
                    continue;
                result = new Result(cur, hits[0],
                    hits[0].EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? "csproj" : "sln",
                    "csharp");
                return true;
            }

            var ts = Path.Combine(cur, "tsconfig.json");
            if (File.Exists(ts))
            {
                result = new Result(cur, ts, "tsconfig", "typescript");
                return true;
            }
        }

        error = "project_not_found";
        return false;
    }
}
