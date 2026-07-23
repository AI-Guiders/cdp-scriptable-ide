using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cdp.ScriptableIde;

/// <summary>Language-agnostic package intents — NuGet / npm projections.</summary>
public static class PackageOps
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<StepResponse> FindAsync(
        ScriptToolBus bus,
        PlanContext plan,
        string query,
        int take = 5,
        CancellationToken ct = default)
    {
        const string kind = "packages.find";
        if (string.IsNullOrWhiteSpace(query))
            return StepResponse.Fail(kind, "query is required");

        take = Math.Clamp(take, 1, 25);
        var lang = ResolveLang(plan);
        StepResponse result;
        if (lang == "typescript")
            result = await FindNpmAsync(query, take, ct).ConfigureAwait(false);
        else if (lang == "csharp")
            result = await FindNugetAsync(query, take, ct).ConfigureAwait(false);
        else
            result = StepResponse.Fail(kind, $"No package projection for language '{lang}'. cdp_open csharp/ts first.");

        bus.RecordLocal("packages", kind, ScriptArgs.From(new { query, take, language = lang }), result.ToJson(),
            skippedDryRun: bus.IsDryRun);
        return result;
    }

    public static async Task<StepResponse> ListAsync(
        ScriptToolBus bus, PlanContext plan, string? projectPath = null, CancellationToken ct = default)
    {
        const string kind = "packages.list";
        plan = WithProjectOverride(plan, projectPath);
        var lang = ResolveLang(plan);
        if (!TryResolveProject(plan, lang, out var project, out var err))
            return FailRecord(bus, kind, err, lang);

        StepResponse result;
        if (bus.IsDryRun)
            result = StepResponse.Success(kind, "dry_run", new { dry_run = true, project, language = lang });
        else if (lang == "typescript")
            result = await RunNpmAsync(kind, project, ["ls", "--depth", "0", "--json"], ct, lang).ConfigureAwait(false);
        else
            result = await RunDotnetAsync(kind, project, ["list", project, "package"], ct, lang).ConfigureAwait(false);

        bus.RecordLocal("packages", kind, ScriptArgs.From(new { project, language = lang }), result.ToJson(),
            skippedDryRun: bus.IsDryRun);
        return result;
    }

    public static async Task<StepResponse> AddAsync(
        ScriptToolBus bus, PlanContext plan, string packageId, string? version = null, string? projectPath = null, CancellationToken ct = default)
    {
        const string kind = "packages.add";
        if (string.IsNullOrWhiteSpace(packageId))
            return StepResponse.Fail(kind, "package id is required");

        plan = WithProjectOverride(plan, projectPath);
        var lang = ResolveLang(plan);
        if (!TryResolveProject(plan, lang, out var project, out var err))
            return FailRecord(bus, kind, err, lang);

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(kind, "dry_run", new { dry_run = true, project, packageId, version, language = lang });
            bus.RecordLocal("packages", kind, ScriptArgs.From(new { project, packageId, version }), dry.ToJson(), skippedDryRun: true);
            return dry;
        }

        StepResponse result;
        if (lang == "typescript")
        {
            var spec = string.IsNullOrWhiteSpace(version) ? packageId : $"{packageId}@{version}";
            result = await RunNpmAsync(kind, project, ["install", spec, "--save"], ct, lang,
                data: new { packageId, version, spec }).ConfigureAwait(false);
        }
        else
        {
            var args = new List<string> { "add", project, "package", packageId };
            if (!string.IsNullOrWhiteSpace(version))
            {
                args.Add("--version");
                args.Add(version!);
            }

            result = await RunDotnetAsync(kind, project, args, ct, lang,
                data: new { packageId, version }).ConfigureAwait(false);
        }

        bus.RecordLocal("packages", kind, ScriptArgs.From(new { project, packageId, version, language = lang }), result.ToJson());
        return result;
    }

    public static async Task<StepResponse> RemoveAsync(
        ScriptToolBus bus, PlanContext plan, string packageId, string? projectPath = null, CancellationToken ct = default)
    {
        const string kind = "packages.remove";
        if (string.IsNullOrWhiteSpace(packageId))
            return StepResponse.Fail(kind, "package id is required");

        plan = WithProjectOverride(plan, projectPath);
        var lang = ResolveLang(plan);
        if (!TryResolveProject(plan, lang, out var project, out var err))
            return FailRecord(bus, kind, err, lang);

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(kind, "dry_run", new { dry_run = true, project, packageId, language = lang });
            bus.RecordLocal("packages", kind, ScriptArgs.From(new { project, packageId }), dry.ToJson(), skippedDryRun: true);
            return dry;
        }

        StepResponse result = lang == "typescript"
            ? await RunNpmAsync(kind, project, ["uninstall", packageId], ct, lang, data: new { packageId }).ConfigureAwait(false)
            : await RunDotnetAsync(kind, project, ["remove", project, "package", packageId], ct, lang, data: new { packageId })
                .ConfigureAwait(false);

        bus.RecordLocal("packages", kind, ScriptArgs.From(new { project, packageId, language = lang }), result.ToJson());
        return result;
    }

    public static async Task<StepResponse> UpdateAsync(
        ScriptToolBus bus, PlanContext plan, string packageId, string? version = null, string? projectPath = null, CancellationToken ct = default)
    {
        // NuGet has no dedicated update in all SDKs — add with version (or latest) is the practical bump.
        // npm: install id@ver or npm update id
        const string kind = "packages.update";
        if (string.IsNullOrWhiteSpace(packageId))
            return StepResponse.Fail(kind, "package id is required");

        plan = WithProjectOverride(plan, projectPath);
        var lang = ResolveLang(plan);
        if (!TryResolveProject(plan, lang, out var project, out var err))
            return FailRecord(bus, kind, err, lang);

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(kind, "dry_run", new { dry_run = true, project, packageId, version, language = lang });
            bus.RecordLocal("packages", kind, ScriptArgs.From(new { project, packageId, version }), dry.ToJson(), skippedDryRun: true);
            return dry;
        }

        StepResponse result;
        if (lang == "typescript")
        {
            if (!string.IsNullOrWhiteSpace(version))
            {
                var spec = $"{packageId}@{version}";
                result = await RunNpmAsync(kind, project, ["install", spec, "--save"], ct, lang,
                    data: new { packageId, version, spec }).ConfigureAwait(false);
            }
            else
            {
                result = await RunNpmAsync(kind, project, ["update", packageId], ct, lang,
                    data: new { packageId }).ConfigureAwait(false);
            }
        }
        else
        {
            var args = new List<string> { "add", project, "package", packageId };
            if (!string.IsNullOrWhiteSpace(version))
            {
                args.Add("--version");
                args.Add(version!);
            }

            result = await RunDotnetAsync(kind, project, args, ct, lang,
                data: new { packageId, version, note = "nuget_bump_via_add" }).ConfigureAwait(false);
        }

        bus.RecordLocal("packages", kind, ScriptArgs.From(new { project, packageId, version, language = lang }), result.ToJson());
        return result;
    }

    public static async Task<StepResponse> OutdatedAsync(
        ScriptToolBus bus, PlanContext plan, string? projectPath = null, CancellationToken ct = default)
    {
        const string kind = "packages.outdated";
        plan = WithProjectOverride(plan, projectPath);
        var lang = ResolveLang(plan);
        if (!TryResolveProject(plan, lang, out var project, out var err))
            return FailRecord(bus, kind, err, lang);

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(kind, "dry_run", new { dry_run = true, project, language = lang });
            bus.RecordLocal("packages", kind, ScriptArgs.From(new { project, language = lang }), dry.ToJson(), skippedDryRun: true);
            return dry;
        }

        StepResponse result = lang == "typescript"
            ? await RunNpmAsync(kind, project, ["outdated", "--json"], ct, lang, allowNonZero: true).ConfigureAwait(false)
            : await RunDotnetAsync(kind, project, ["list", project, "package", "--outdated"], ct, lang).ConfigureAwait(false);

        bus.RecordLocal("packages", kind, ScriptArgs.From(new { project, language = lang }), result.ToJson());
        return result;
    }

    public static string ResolveLang(PlanContext plan)
    {
        var lang = plan.Language?.Trim().ToLowerInvariant();
        if (lang is "typescript" or "ts" or "tsx")
            return "typescript";
        if (lang is "csharp" or "cs" or "c#")
            return "csharp";

        var anchor = plan.SolutionOrProjectPath ?? "";
        if (anchor.EndsWith("tsconfig.json", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(plan.WorkRoot, "package.json")))
            return "typescript";
        if (anchor.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || anchor.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return "csharp";
        return lang is { Length: > 0 } ? lang : "csharp";
    }

    private static PlanContext WithProjectOverride(PlanContext plan, string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return plan;
        return new PlanContext
        {
            PrimaryRoot = plan.PrimaryRoot,
            WorkRoot = plan.WorkRoot,
            PlanId = plan.PlanId,
            SolutionOrProjectPath = Path.GetFullPath(projectPath),
            Language = plan.Language,
            Settings = plan.Settings
        };
    }

    public static bool TryResolveProject(PlanContext plan, string lang, out string project, out string error)
    {
        project = "";
        error = "";
        if (lang == "typescript")
        {
            var root = plan.WorkRoot;
            if (!string.IsNullOrWhiteSpace(plan.SolutionOrProjectPath)
                && plan.SolutionOrProjectPath!.EndsWith("tsconfig.json", StringComparison.OrdinalIgnoreCase))
                root = Path.GetDirectoryName(plan.SolutionOrProjectPath)!;
            else if (Directory.Exists(plan.WorkRoot))
                root = plan.WorkRoot;

            var pkg = FindUp(root, "package.json");
            if (pkg is null)
            {
                error = "No package.json found (typescript). cdp_open a JS/TS package root.";
                return false;
            }

            project = Path.GetDirectoryName(pkg)!;
            return true;
        }

        // csharp
        var path = plan.SolutionOrProjectPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "No csharp project in session. cdp_open a .csproj (preferred) or .sln.";
            return false;
        }

        path = Path.GetFullPath(path);
        if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
        {
            project = path;
            return true;
        }

        if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            error = "packages.add/remove need a .csproj — open a project file or pass path= to Packages (session is .sln).";
            return false;
        }

        error = $"Unsupported project anchor: {path}";
        return false;
    }

    private static StepResponse FailRecord(ScriptToolBus bus, string kind, string error, string lang)
    {
        var fail = StepResponse.Fail(kind, error, new { language = lang });
        bus.RecordLocal("packages", kind, ScriptArgs.From(new { language = lang }), fail.ToJson());
        return fail;
    }

    private static async Task<StepResponse> FindNugetAsync(string query, int take, CancellationToken ct)
    {
        const string kind = "packages.find";
        var url = VendorCatalog.FormatNugetSearch(query, take);
        try
        {
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var payload = await resp.Content.ReadFromJsonAsync<NugetSearchResponse>(cancellationToken: ct)
                .ConfigureAwait(false);
            var hits = payload?.Data?.Select(d => new
            {
                id = d.Id,
                version = d.Version,
                description = Trunc(d.Description, 200),
                totalDownloads = d.TotalDownloads
            }).ToArray() ?? [];
            return StepResponse.Success(kind, $"nuget:{hits.Length}", new
            {
                projection = "nuget",
                query,
                take,
                totalHits = payload?.TotalHits,
                results = hits
            });
        }
        catch (Exception ex)
        {
            return StepResponse.Fail(kind, $"nuget search failed: {ex.Message}", new { query });
        }
    }

    private static async Task<StepResponse> FindNpmAsync(string query, int take, CancellationToken ct)
    {
        const string kind = "packages.find";
        var npm = ResolveNpmCli("npm");
        var (code, stdout, stderr) = await ProcessUtil.RunAsync(
            npm, ["search", query, "--json", "--searchlimit", take.ToString()],
            workingDirectory: null, env: null, ct).ConfigureAwait(false);
        if (code != 0 && string.IsNullOrWhiteSpace(stdout))
            return StepResponse.Fail(kind, $"npm search failed: {stderr}", new { query, exit_code = code });

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdout) ? "[]" : stdout);
            var results = new List<object>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray().Take(take))
                {
                    results.Add(new
                    {
                        id = el.TryGetProperty("name", out var n) ? n.GetString() : null,
                        version = el.TryGetProperty("version", out var v) ? v.GetString() : null,
                        description = Trunc(el.TryGetProperty("description", out var d) ? d.GetString() : null, 200)
                    });
                }
            }

            return StepResponse.Success(kind, $"npm:{results.Count}", new
            {
                projection = "npm",
                query,
                take,
                results
            });
        }
        catch (Exception ex)
        {
            return StepResponse.Fail(kind, $"npm search parse failed: {ex.Message}", new { stdout = Trunc(stdout, 500) });
        }
    }

    private static async Task<StepResponse> RunDotnetAsync(
        string kind,
        string project,
        IReadOnlyList<string> args,
        CancellationToken ct,
        string lang,
        object? data = null)
    {
        var cwd = Path.GetDirectoryName(project) ?? Environment.CurrentDirectory;
        var (code, stdout, stderr) = await ProcessUtil.RunAsync("dotnet", args, cwd, null, ct).ConfigureAwait(false);
        var ok = code == 0;
        var payload = new
        {
            projection = "nuget",
            language = lang,
            project,
            exit_code = code,
            stdout = Trunc(stdout, 6000),
            stderr = Trunc(stderr, 2000),
            data
        };
        return ok
            ? StepResponse.Success(kind, "ok", payload)
            : StepResponse.Fail(kind, $"dotnet exit {code}", payload);
    }

    private static async Task<StepResponse> RunNpmAsync(
        string kind,
        string packageDir,
        IReadOnlyList<string> args,
        CancellationToken ct,
        string lang,
        object? data = null,
        bool allowNonZero = false)
    {
        var npm = ResolveNpmCli("npm");
        var (code, stdout, stderr) = await ProcessUtil.RunAsync(npm, args, packageDir, null, ct).ConfigureAwait(false);
        var ok = code == 0 || allowNonZero;
        var payload = new
        {
            projection = "npm",
            language = lang,
            project = packageDir,
            exit_code = code,
            stdout = Trunc(stdout, 6000),
            stderr = Trunc(stderr, 2000),
            data
        };
        // npm outdated returns non-zero when outdated exist — still success with data
        if (allowNonZero)
            return StepResponse.Success(kind, code == 0 ? "up_to_date" : "has_outdated", payload);
        return ok
            ? StepResponse.Success(kind, "ok", payload)
            : StepResponse.Fail(kind, $"npm exit {code}", payload);
    }

    private static string? FindUp(string startDir, string fileName)
    {
        var dir = startDir;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir)
                break;
            dir = parent;
        }

        return null;
    }

    private static string ResolveNpmCli(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            var pf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "nodejs", name + ".cmd");
            if (File.Exists(pf))
                return pf;
            var pf86 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "nodejs", name + ".cmd");
            if (File.Exists(pf86))
                return pf86;
            return name + ".cmd";
        }

        return name;
    }

    private static string? Trunc(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return s;
        return s.Length <= max ? s : s[..max] + "…";
    }

    private sealed class NugetSearchResponse
    {
        [JsonPropertyName("totalHits")]
        public long TotalHits { get; set; }

        [JsonPropertyName("data")]
        public List<NugetHit>? Data { get; set; }
    }

    private sealed class NugetHit
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("totalDownloads")]
        public long TotalDownloads { get; set; }
    }
}
