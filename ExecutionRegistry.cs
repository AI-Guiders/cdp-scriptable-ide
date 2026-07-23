namespace Cdp.ScriptableIde;

/// <summary>CI-like step registry (ADR 0040 spirit: executable + arguments in TOML, no rebuild).</summary>
public sealed class ExecutionRegistry
{
    private readonly Dictionary<string, ExecutionStepDef> _steps = new(StringComparer.OrdinalIgnoreCase);

    public string? Source { get; private set; }
    public IReadOnlyDictionary<string, ExecutionStepDef> Steps => _steps;

    public void Clear()
    {
        _steps.Clear();
        Source = null;
    }

    public void LoadFromToml(string tomlOrPath, PlanContext plan)
    {
        string text;
        string source;
        var trimmed = tomlOrPath.Trim();
        if (LooksLikeInlineToml(trimmed))
        {
            text = trimmed;
            source = "(inline)";
        }
        else
        {
            var path = ResolveConfigPath(trimmed, plan);
            text = File.ReadAllText(path);
            source = path;
        }

        _steps.Clear();
        Source = source;
        foreach (var step in ExecutionTomlLite.ParseSteps(text))
            _steps[step.Id] = step;

        if (_steps.Count == 0)
            throw new InvalidOperationException("Execution config requires [steps.<id>] tables with executable.");
    }

    public ExecutionStepDef GetRequired(string stepId)
    {
        if (_steps.TryGetValue(stepId, out var def))
            return def;
        var known = _steps.Count == 0
            ? "(no config loaded — call ExecEnvironment.Configuration.Set first)"
            : string.Join(", ", _steps.Keys.Order(StringComparer.OrdinalIgnoreCase));
        throw new ArgumentException($"Unknown execution step '{stepId}'. Known: {known}");
    }

    private static bool LooksLikeInlineToml(string s) =>
        s.Contains('[') && (s.Contains("[steps", StringComparison.OrdinalIgnoreCase) || s.Contains('\n'));

    private static string ResolveConfigPath(string path, PlanContext plan)
    {
        if (Path.IsPathRooted(path) && File.Exists(path))
            return Path.GetFullPath(path);

        foreach (var root in new[] { plan.WorkRoot, plan.PrimaryRoot, Environment.CurrentDirectory })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var candidate = Path.GetFullPath(Path.Combine(root, path));
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Execution config not found: {path}");
    }
}

public sealed record ExecutionStepDef(
    string Id,
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory);

/// <summary>
/// Tiny subset parser for [steps.id] / executable / arguments / working_directory.
/// Avoids Tomlyn version skew between hosts; full TOML can come later via shared binder.
/// </summary>
internal static class ExecutionTomlLite
{
    public static IEnumerable<ExecutionStepDef> ParseSteps(string text)
    {
        string? currentId = null;
        string? executable = null;
        string? workingDirectory = null;
        List<string> arguments = [];

        ExecutionStepDef? Flush()
        {
            if (currentId is null)
                return null;
            if (string.IsNullOrWhiteSpace(executable))
                throw new InvalidOperationException($"steps.{currentId}: executable is required.");
            var def = new ExecutionStepDef(currentId, executable!, arguments.ToArray(), workingDirectory);
            currentId = null;
            executable = null;
            workingDirectory = null;
            arguments = [];
            return def;
        }

        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (Flush() is { } prev)
                    yield return prev;

                var header = line[1..^1].Trim();
                if (header.StartsWith("steps.", StringComparison.OrdinalIgnoreCase))
                    currentId = header["steps.".Length..].Trim().Trim('"');
                else if (header.Equals("steps", StringComparison.OrdinalIgnoreCase))
                    currentId = null; // container only
                else
                    currentId = null;
                continue;
            }

            if (currentId is null)
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (key.Equals("executable", StringComparison.OrdinalIgnoreCase))
                executable = Unquote(value);
            else if (key.Equals("working_directory", StringComparison.OrdinalIgnoreCase))
                workingDirectory = Unquote(value);
            else if (key.Equals("arguments", StringComparison.OrdinalIgnoreCase))
                arguments = ParseArray(value);
        }

        if (Flush() is { } last)
            yield return last;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1];
        return value;
    }

    private static List<string> ParseArray(string value)
    {
        value = value.Trim();
        if (!value.StartsWith('[') || !value.EndsWith(']'))
            return [Unquote(value)];

        var inner = value[1..^1].Trim();
        if (inner.Length == 0)
            return [];

        var list = new List<string>();
        var i = 0;
        while (i < inner.Length)
        {
            while (i < inner.Length && (char.IsWhiteSpace(inner[i]) || inner[i] == ','))
                i++;
            if (i >= inner.Length)
                break;
            if (inner[i] is '"' or '\'')
            {
                var q = inner[i++];
                var start = i;
                while (i < inner.Length && inner[i] != q)
                {
                    if (inner[i] == '\\' && i + 1 < inner.Length)
                        i += 2;
                    else
                        i++;
                }
                list.Add(inner[start..i].Replace("\\\"", "\"", StringComparison.Ordinal));
                if (i < inner.Length && inner[i] == q)
                    i++;
            }
            else
            {
                var start = i;
                while (i < inner.Length && inner[i] != ',')
                    i++;
                list.Add(inner[start..i].Trim());
            }
        }
        return list;
    }
}
