namespace Cdp.ScriptableIde;

/// <summary>Project lifecycle intents — create/list (open/close = session meta).</summary>
public static class ProjectOps
{
    public static async Task<StepResponse> CreateAsync(
        ScriptToolBus bus,
        PlanContext plan,
        string outputDir,
        string? name = null,
        string template = "console",
        TfmPolicy tfmPolicy = TfmPolicy.PreferMostUsed,
        string? tfm = null,
        EnginePolicy enginePolicy = EnginePolicy.PreferMostUsed,
        string? engines = null,
        bool force = false,
        CancellationToken ct = default)
    {
        const string kind = "projects.create";
        if (string.IsNullOrWhiteSpace(outputDir))
            return StepResponse.Fail(kind, "output dir is required");

        var lang = PackageOps.ResolveLang(plan);
        outputDir = Path.GetFullPath(
            Path.IsPathRooted(outputDir) ? outputDir : Path.Combine(plan.WorkRoot, outputDir));
        name ??= new DirectoryInfo(outputDir).Name;
        if (string.IsNullOrWhiteSpace(name))
            return StepResponse.Fail(kind, "project name is required");

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(kind, "dry_run", new
            {
                dry_run = true,
                language = lang,
                outputDir,
                name,
                template,
                tfmPolicy = tfmPolicy.ToString(),
                tfm,
                enginePolicy = enginePolicy.ToString(),
                engines
            });
            bus.RecordLocal("projects", kind, ScriptArgs.From(new { outputDir, name, template }), dry.ToJson(),
                skippedDryRun: true);
            return dry;
        }

        StepResponse result;
        if (lang == "typescript")
            result = await CreateTypescriptAsync(kind, outputDir, name, enginePolicy, engines, plan.WorkRoot, ct)
                .ConfigureAwait(false);
        else
            result = await CreateCsharpAsync(kind, outputDir, name, template, tfmPolicy, tfm, force, plan.WorkRoot, ct)
                .ConfigureAwait(false);

        bus.RecordLocal("projects", kind, ScriptArgs.From(new { outputDir, name, template, language = lang }),
            result.ToJson());
        return result;
    }

    /// <summary>Project map: curated templates + optional installed list + existing projects/solutions.</summary>
    public static async Task<StepResponse> SceneAsync(
        ScriptToolBus bus,
        PlanContext plan,
        string? root = null,
        bool includeInstalled = false,
        int maxExisting = ProjectScene.MaxExistingDefault,
        int maxInstalled = ProjectScene.MaxInstalledDefault,
        CancellationToken ct = default)
    {
        const string kind = "projects.scene";
        maxExisting = Math.Clamp(maxExisting, 1, 200);
        maxInstalled = Math.Clamp(maxInstalled, 1, 200);

        var scan = string.IsNullOrWhiteSpace(root)
            ? plan.WorkRoot
            : Path.GetFullPath(Path.IsPathRooted(root!) ? root! : Path.Combine(plan.WorkRoot, root!));

        var habitat = ProjectScene.IsHostHabitatRoot(scan);
        object[]? installed = null;
        string? installedNote = null;
        if (includeInstalled)
        {
            var procCwd = habitat ? Path.GetTempPath() : scan;
            var (code, stdout, stderr) = await ProcessUtil.RunAsync(
                "dotnet", ["new", "list", "--type", "project"], procCwd, null, ct).ConfigureAwait(false);
            if (code == 0)
            {
                var cards = ProjectScene.ParseDotnetNewList(stdout, maxInstalled);
                installed = cards.Select(c => new
                {
                    id = c.Id,
                    title = c.Title,
                    language = c.Language,
                    tags = c.Tags,
                    create_via = c.CreateVia
                }).ToArray<object>();
                installedNote = $"parsed:{cards.Count}";
            }
            else
            {
                installedNote = $"dotnet new list exit {code}: {Trunc(stderr, 400)}";
            }
        }

        var existingProjects = Array.Empty<object>();
        var existingSolutions = Array.Empty<object>();
        string? scanNote = null;
        if (habitat)
        {
            scanNote = ProjectScene.HostHabitatScanNote;
        }
        else if (Directory.Exists(scan))
        {
            try
            {
                existingProjects = Directory.EnumerateFiles(scan, "*.csproj", SearchOption.AllDirectories)
                    .Take(maxExisting)
                    .Select(p => (object)new { path = p, kind = "csproj" })
                    .Concat(Directory.EnumerateFiles(scan, "tsconfig.json", SearchOption.AllDirectories)
                        .Take(Math.Max(0, maxExisting / 4))
                        .Select(p => (object)new { path = p, kind = "tsconfig" }))
                    .Take(maxExisting)
                    .ToArray();
                existingSolutions = Directory.EnumerateFiles(scan, "*.sln", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.EnumerateFiles(scan, "*.slnx", SearchOption.TopDirectoryOnly))
                    .Take(20)
                    .Select(p => (object)new { path = p })
                    .ToArray();
            }
            catch (UnauthorizedAccessException ex)
            {
                scanNote = $"scan skipped (access denied): {Trunc(ex.Message, 200)}";
            }
            catch (IOException ex)
            {
                scanNote = $"scan skipped (io): {Trunc(ex.Message, 200)}";
            }
        }

        var curated = ProjectScene.Curated.Select(c => new
        {
            id = c.Id,
            title = c.Title,
            language = c.Language,
            tags = c.Tags,
            create_via = c.CreateVia,
            template = c.Id == "typescript" ? (string?)null : c.Id
        }).ToArray();

        var payload = new
        {
            schema = ProjectScene.SchemaVersion,
            session = new
            {
                language = plan.Language,
                work_root = plan.WorkRoot,
                primary_root = plan.PrimaryRoot,
                solution_or_project = plan.SolutionOrProjectPath
            },
            scan_root = scan,
            scan_note = scanNote,
            templates_curated = curated,
            templates_installed = installed,
            templates_installed_note = installedNote,
            policies = ProjectScene.PolicyEnums(),
            existing = new { projects = existingProjects, solutions = existingSolutions },
            next = new
            {
                create = "cdp_project_create / Projects.Create",
                hint = "Pick templates_curated[].id as template= (csharp). typescript: set session language then create (npm_init). Prefer scene before inventing files."
            }
        };

        var result = StepResponse.Success(kind,
            $"curated:{curated.Length};existing:{existingProjects.Length}", payload);
        bus.RecordLocal("projects", kind, ScriptArgs.From(new { root = scan, includeInstalled }), result.ToJson());
        return result;
    }

    public static Task<StepResponse> ListAsync(
        ScriptToolBus bus,
        PlanContext plan,
        string? root = null,
        CancellationToken ct = default)
    {
        _ = ct;
        const string kind = "projects.list";
        var scan = string.IsNullOrWhiteSpace(root)
            ? plan.WorkRoot
            : Path.GetFullPath(Path.IsPathRooted(root) ? root! : Path.Combine(plan.WorkRoot, root!));
        if (ProjectScene.IsHostHabitatRoot(scan))
        {
            var habitat = StepResponse.Success(kind, "habitat_skip", new
            {
                root = scan,
                items = Array.Empty<object>(),
                scan_note = ProjectScene.HostHabitatScanNote
            });
            bus.RecordLocal("projects", kind, ScriptArgs.From(new { root = scan }), habitat.ToJson());
            return Task.FromResult(habitat);
        }

        if (!Directory.Exists(scan))
        {
            var fail = StepResponse.Fail(kind, $"root not found: {scan}");
            bus.RecordLocal("projects", kind, ScriptArgs.From(new { root = scan }), fail.ToJson());
            return Task.FromResult(fail);
        }

        object[] items;
        try
        {
            var csprojs = Directory.EnumerateFiles(scan, "*.csproj", SearchOption.AllDirectories)
                .Take(100).Select(p => new { path = p, kind = "csproj" });
            var tsconfigs = Directory.EnumerateFiles(scan, "tsconfig.json", SearchOption.AllDirectories)
                .Take(100).Select(p => new { path = p, kind = "tsconfig" });
            items = csprojs.Concat(tsconfigs).Take(120).Cast<object>().ToArray();
        }
        catch (UnauthorizedAccessException ex)
        {
            var denied = StepResponse.Fail(kind, $"access denied: {Trunc(ex.Message, 200)}");
            bus.RecordLocal("projects", kind, ScriptArgs.From(new { root = scan }), denied.ToJson());
            return Task.FromResult(denied);
        }

        var result = StepResponse.Success(kind, $"found:{items.Length}", new { root = scan, items });
        bus.RecordLocal("projects", kind, ScriptArgs.From(new { root = scan }), result.ToJson());
        return Task.FromResult(result);
    }

    private static async Task<StepResponse> CreateCsharpAsync(
        string kind,
        string outputDir,
        string name,
        string template,
        TfmPolicy policy,
        string? specifiedTfm,
        bool force,
        string scanRoot,
        CancellationToken ct)
    {
        string tfm;
        string tfmDetail;
        try
        {
            (tfm, tfmDetail) = await TfmResolver.ResolveAsync(policy, specifiedTfm, scanRoot, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return StepResponse.Fail(kind, ex.Message);
        }

        Directory.CreateDirectory(outputDir);
        var args = new List<string>
        {
            "new", template,
            "-n", name,
            "-o", outputDir,
            "-f", tfm
        };
        if (force)
            args.Add("--force");

        var (code, stdout, stderr) = await ProcessUtil.RunAsync("dotnet", args, outputDir, null, ct)
            .ConfigureAwait(false);
        var csproj = Path.Combine(outputDir, name + ".csproj");
        if (!File.Exists(csproj))
        {
            // template may use folder name
            csproj = Directory.EnumerateFiles(outputDir, "*.csproj").FirstOrDefault() ?? csproj;
        }

        var payload = new
        {
            projection = "dotnet_new",
            template,
            name,
            outputDir,
            tfm,
            tfm_policy = policy.ToString(),
            tfm_detail = tfmDetail,
            project = File.Exists(csproj) ? csproj : null,
            exit_code = code,
            stdout = Trunc(stdout, 4000),
            stderr = Trunc(stderr, 2000)
        };
        string? promotedEntry = null;
        if (code == 0 && File.Exists(csproj))
        {
            TestPackageBundle.TryExcludeUnderscoreScratchDirs(new PlanContext
            {
                PrimaryRoot = outputDir,
                WorkRoot = outputDir,
                SolutionOrProjectPath = csproj,
                Language = "csharp"
            });
            promotedEntry = ProjectScaffoldHygiene.TryPromoteClass1(outputDir, name);
        }

        var successPayload = new
        {
            projection = "dotnet_new",
            template,
            name,
            outputDir,
            tfm,
            tfm_policy = policy.ToString(),
            tfm_detail = tfmDetail,
            project = File.Exists(csproj) ? csproj : null,
            entry = promotedEntry,
            exit_code = code,
            stdout = Trunc(stdout, 4000),
            stderr = Trunc(stderr, 2000)
        };

        return code == 0
            ? StepResponse.Success(kind, $"created:{tfm}", successPayload)
            : StepResponse.Fail(kind, $"dotnet new exit {code}", payload);
    }

    private static async Task<StepResponse> CreateTypescriptAsync(
        string kind,
        string outputDir,
        string name,
        EnginePolicy enginePolicy,
        string? specifiedEngines,
        string scanRoot,
        CancellationToken ct)
    {
        string engineRange;
        string engineDetail;
        try
        {
            (engineRange, engineDetail) = await EngineResolver.ResolveAsync(
                enginePolicy, specifiedEngines, scanRoot, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return StepResponse.Fail(kind, ex.Message);
        }

        Directory.CreateDirectory(outputDir);
        var npm = ResolveNpm();
        var (code, stdout, stderr) = await ProcessUtil.RunAsync(npm, ["init", "-y"], outputDir, null, ct)
            .ConfigureAwait(false);
        var pkg = Path.Combine(outputDir, "package.json");
        if (File.Exists(pkg))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(
                    await File.ReadAllTextAsync(pkg, ct).ConfigureAwait(false));
                var dict = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
                foreach (var p in doc.RootElement.EnumerateObject())
                    dict[p.Name] = p.Value.Clone();
                dict["name"] = System.Text.Json.JsonSerializer.SerializeToElement(name);
                dict["engines"] = System.Text.Json.JsonSerializer.SerializeToElement(new { node = engineRange });
                await File.WriteAllTextAsync(pkg,
                    System.Text.Json.JsonSerializer.Serialize(dict, new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true
                    }), ct).ConfigureAwait(false);
            }
            catch
            {
                // keep npm init output
            }
        }

        var tsconfig = Path.Combine(outputDir, "tsconfig.json");
        if (!File.Exists(tsconfig))
        {
            await File.WriteAllTextAsync(tsconfig, """
                {
                  "compilerOptions": {
                    "strict": true,
                    "target": "ES2022",
                    "module": "ESNext",
                    "moduleResolution": "bundler",
                    "outDir": "dist",
                    "skipLibCheck": true
                  },
                  "include": ["src/**/*"]
                }
                """, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.Combine(outputDir, "src"));
            await File.WriteAllTextAsync(Path.Combine(outputDir, "src", "index.ts"),
                "export function main() { return \"ok\"; }\n", ct).ConfigureAwait(false);
        }

        var payload = new
        {
            projection = "npm_init",
            name,
            outputDir,
            engines = engineRange,
            engine_policy = enginePolicy.ToString(),
            engine_detail = engineDetail,
            package_json = File.Exists(pkg) ? pkg : null,
            tsconfig = File.Exists(tsconfig) ? tsconfig : null,
            exit_code = code,
            stdout = Trunc(stdout, 2000),
            stderr = Trunc(stderr, 1000)
        };
        return code == 0
            ? StepResponse.Success(kind, "created:typescript", payload)
            : StepResponse.Fail(kind, $"npm init exit {code}", payload);
    }

    private static string ResolveNpm()
    {
        if (!OperatingSystem.IsWindows())
            return "npm";
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd");
        return File.Exists(pf) ? pf : "npm.cmd";
    }

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
