namespace Cdp.ScriptableIde;

/// <summary>
/// Solution lifecycle — create/list + membership (add/remove/list projects).
/// csharp-only for now (dotnet new sln / dotnet sln); open/close = session meta.
/// </summary>
public static class SolutionOps
{
    public static async Task<StepResponse> CreateAsync(
        ScriptToolBus bus,
        PlanContext plan,
        string outputDir,
        string? name = null,
        bool force = false,
        bool open = false,
        CancellationToken ct = default)
    {
        const string kind = "solutions.create";
        if (string.IsNullOrWhiteSpace(outputDir))
            return FailRecord(bus, kind, "output dir is required");

        outputDir = IdePath.Resolve(plan.WorkRoot, outputDir);
        name ??= new DirectoryInfo(outputDir).Name;
        if (string.IsNullOrWhiteSpace(name))
            return FailRecord(bus, kind, "solution name is required");

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(kind, "dry_run", new { dry_run = true, outputDir, name });
            bus.RecordLocal("solutions", kind, ScriptArgs.From(new { outputDir, name }), dry.ToJson(),
                skippedDryRun: true);
            return dry;
        }

        Directory.CreateDirectory(outputDir);
        var args = new List<string> { "new", "sln", "-n", name, "-o", outputDir };
        if (force)
            args.Add("--force");

        var (code, stdout, stderr) = await ProcessUtil.RunAsync("dotnet", args, outputDir, null, ct)
            .ConfigureAwait(false);

        var sln = FindSlnFile(outputDir, name);
        var payload = new
        {
            projection = "dotnet_new_sln",
            name,
            outputDir,
            solution = sln,
            exit_code = code,
            stdout = Trunc(stdout, 2000),
            stderr = Trunc(stderr, 1000),
            open_hint = open
        };
        var result = code == 0 && sln is not null
            ? StepResponse.Success(kind, "created", payload)
            : StepResponse.Fail(kind, $"dotnet new sln exit {code}", payload);
        bus.RecordLocal("solutions", kind, ScriptArgs.From(new { outputDir, name }), result.ToJson());
        return result;
    }

    public static Task<StepResponse> ListAsync(
        ScriptToolBus bus,
        PlanContext plan,
        string? root = null,
        CancellationToken ct = default)
    {
        _ = ct;
        const string kind = "solutions.list";
        var scan = string.IsNullOrWhiteSpace(root)
            ? plan.WorkRoot
            : IdePath.Resolve(plan.WorkRoot, root!);
        if (!Directory.Exists(scan))
        {
            var fail = StepResponse.Fail(kind, $"root not found: {scan}");
            bus.RecordLocal("solutions", kind, ScriptArgs.From(new { root = scan }), fail.ToJson());
            return Task.FromResult(fail);
        }

        var items = Directory.EnumerateFiles(scan, "*.sln", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(scan, "*.slnx", SearchOption.AllDirectories))
            .Take(80)
            .Select(p => new { path = p, kind = Path.GetExtension(p).TrimStart('.').ToLowerInvariant() })
            .ToArray();
        var result = StepResponse.Success(kind, $"found:{items.Length}", new { root = scan, items });
        bus.RecordLocal("solutions", kind, ScriptArgs.From(new { root = scan }), result.ToJson());
        return Task.FromResult(result);
    }

    public static async Task<StepResponse> ListProjectsAsync(
        ScriptToolBus bus,
        PlanContext plan,
        string? solution = null,
        CancellationToken ct = default)
    {
        const string kind = "solutions.list_projects";
        if (!TryResolveSln(plan, solution, out var sln, out var err))
            return FailRecord(bus, kind, err!);

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(kind, "dry_run", new { dry_run = true, solution = sln });
            bus.RecordLocal("solutions", kind, ScriptArgs.From(new { solution = sln }), dry.ToJson(),
                skippedDryRun: true);
            return dry;
        }

        var cwd = Path.GetDirectoryName(sln)!;
        var (code, stdout, stderr) = await ProcessUtil.RunAsync("dotnet", ["sln", sln, "list"], cwd, null, ct)
            .ConfigureAwait(false);
        var projects = ParseSlnList(stdout);
        var payload = new
        {
            projection = "dotnet_sln_list",
            solution = sln,
            projects,
            exit_code = code,
            stdout = Trunc(stdout, 4000),
            stderr = Trunc(stderr, 1000)
        };
        var result = code == 0
            ? StepResponse.Success(kind, $"projects:{projects.Count}", payload)
            : StepResponse.Fail(kind, $"dotnet sln list exit {code}", payload);
        bus.RecordLocal("solutions", kind, ScriptArgs.From(new { solution = sln }), result.ToJson());
        return result;
    }

    public static async Task<StepResponse> AddProjectAsync(
        ScriptToolBus bus,
        PlanContext plan,
        string projectPath,
        string? solution = null,
        bool inRoot = false,
        string? solutionFolder = null,
        CancellationToken ct = default)
    {
        const string kind = "solutions.add_project";
        if (string.IsNullOrWhiteSpace(projectPath))
            return FailRecord(bus, kind, "project path is required");
        if (!TryResolveSln(plan, solution, out var sln, out var err))
            return FailRecord(bus, kind, err!);

        var proj = IdePath.Resolve(plan.WorkRoot, projectPath);
        if (!File.Exists(proj))
            return FailRecord(bus, kind, $"project not found: {proj}");

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(kind, "dry_run",
                new { dry_run = true, solution = sln, project = proj, inRoot, solutionFolder });
            bus.RecordLocal("solutions", kind, ScriptArgs.From(new { solution = sln, project = proj }), dry.ToJson(),
                skippedDryRun: true);
            return dry;
        }

        var args = new List<string> { "sln", sln, "add", proj };
        if (inRoot)
            args.Add("--in-root");
        else if (!string.IsNullOrWhiteSpace(solutionFolder))
        {
            args.Add("--solution-folder");
            args.Add(solutionFolder!);
        }

        var cwd = Path.GetDirectoryName(sln)!;
        var (code, stdout, stderr) = await ProcessUtil.RunAsync("dotnet", args, cwd, null, ct)
            .ConfigureAwait(false);
        var payload = new
        {
            projection = "dotnet_sln_add",
            solution = sln,
            project = proj,
            exit_code = code,
            stdout = Trunc(stdout, 2000),
            stderr = Trunc(stderr, 1000)
        };
        var result = code == 0
            ? StepResponse.Success(kind, "added", payload)
            : StepResponse.Fail(kind, $"dotnet sln add exit {code}", payload);
        bus.RecordLocal("solutions", kind, ScriptArgs.From(new { solution = sln, project = proj }), result.ToJson());
        return result;
    }

    public static async Task<StepResponse> RemoveProjectAsync(
        ScriptToolBus bus,
        PlanContext plan,
        string projectPath,
        string? solution = null,
        CancellationToken ct = default)
    {
        const string kind = "solutions.remove_project";
        if (string.IsNullOrWhiteSpace(projectPath))
            return FailRecord(bus, kind, "project path is required");
        if (!TryResolveSln(plan, solution, out var sln, out var err))
            return FailRecord(bus, kind, err!);

        var proj = IdePath.Resolve(plan.WorkRoot, projectPath);
        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(kind, "dry_run", new { dry_run = true, solution = sln, project = proj });
            bus.RecordLocal("solutions", kind, ScriptArgs.From(new { solution = sln, project = proj }), dry.ToJson(),
                skippedDryRun: true);
            return dry;
        }

        var cwd = Path.GetDirectoryName(sln)!;
        var (code, stdout, stderr) = await ProcessUtil
            .RunAsync("dotnet", ["sln", sln, "remove", proj], cwd, null, ct).ConfigureAwait(false);
        var payload = new
        {
            projection = "dotnet_sln_remove",
            solution = sln,
            project = proj,
            exit_code = code,
            stdout = Trunc(stdout, 2000),
            stderr = Trunc(stderr, 1000)
        };
        var result = code == 0
            ? StepResponse.Success(kind, "removed", payload)
            : StepResponse.Fail(kind, $"dotnet sln remove exit {code}", payload);
        bus.RecordLocal("solutions", kind, ScriptArgs.From(new { solution = sln, project = proj }), result.ToJson());
        return result;
    }

    internal static bool TryResolveSln(PlanContext plan, string? solution, out string sln, out string? error)
    {
        sln = "";
        error = null;
        if (!string.IsNullOrWhiteSpace(solution))
        {
            sln = IdePath.Resolve(plan.WorkRoot, solution!);
            if (!File.Exists(sln))
            {
                error = $"solution not found: {sln}";
                return false;
            }

            return true;
        }

        var anchor = plan.SolutionOrProjectPath;
        if (!string.IsNullOrWhiteSpace(anchor) && IsSln(anchor!))
        {
            sln = Path.GetFullPath(anchor!);
            if (File.Exists(sln))
                return true;
        }

        var found = Directory.Exists(plan.WorkRoot)
            ? Directory.EnumerateFiles(plan.WorkRoot, "*.sln", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(plan.WorkRoot, "*.slnx", SearchOption.TopDirectoryOnly))
                .Take(3)
                .ToArray()
            : [];
        if (found.Length == 1)
        {
            sln = found[0];
            return true;
        }

        error = found.Length == 0
            ? "no solution in session/work root — pass solution= path"
            : $"multiple solutions in work root ({found.Length}) — pass solution= path";
        return false;
    }

    private static bool IsSln(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    private static string? FindSlnFile(string outputDir, string name)
    {
        var candidates = new[]
        {
            Path.Combine(outputDir, name + ".slnx"),
            Path.Combine(outputDir, name + ".sln"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return c;
        }

        return Directory.EnumerateFiles(outputDir, "*.slnx").Concat(
                Directory.EnumerateFiles(outputDir, "*.sln"))
            .FirstOrDefault();
    }

    private static List<string> ParseSlnList(string stdout)
    {
        var list = new List<string>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Contains("-----", StringComparison.Ordinal))
                continue;
            if (line.Contains("Project(s)", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                line.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
                line.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
                list.Add(line);
        }

        return list;
    }

    private static StepResponse FailRecord(ScriptToolBus bus, string kind, string error)
    {
        var fail = StepResponse.Fail(kind, error);
        bus.RecordLocal("solutions", kind, ScriptArgs.From(new { }), fail.ToJson());
        return fail;
    }

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}

internal static class IdePath
{
    public static string Resolve(string workRoot, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workRoot, path));
}
