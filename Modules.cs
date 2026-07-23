namespace Cdp.ScriptableIde;

/// <summary>
/// Language imports — csharp <c>using</c>, python <c>import</c>, typescript <c>import</c>.
/// </summary>
public static class Modules
{
    public static ModulesImport Import(string name) => new(name);
}

public sealed class ModulesImport(string name)
{
    private string? _into;

    public ModulesImport Into(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _into = filePath.Trim();
        return this;
    }

    public ModulesImport Into(Anchor fileAnchor)
    {
        ArgumentNullException.ThrowIfNull(fileAnchor);
        var span = fileAnchor.ToSpan();
        if (string.IsNullOrWhiteSpace(span.File))
            throw new InvalidOperationException("Modules.Import.Into(Anchor) needs File(path)");
        _into = span.File;
        return this;
    }

    public Task<StepResponse> ApplyAsync(IScriptToolBus bus, PlanContext plan, CancellationToken ct = default) =>
        ModulesRunner.ApplyImportAsync(bus, plan, name, _into, ct);
}

/// <summary>CSX surface — bus/plan from globals via extension-style Apply on ScriptGlobals later; use ModulesFacade.</summary>
public sealed class ModulesFacade(IScriptToolBus bus, PlanContext plan)
{
    public ModulesImportBind Import(string name) => new(bus, plan, name);
}

public sealed class ModulesImportBind(IScriptToolBus bus, PlanContext plan, string name)
{
    private string? _into;

    public ModulesImportBind Into(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _into = filePath.Trim();
        return this;
    }

    public ModulesImportBind Into(Anchor fileAnchor)
    {
        ArgumentNullException.ThrowIfNull(fileAnchor);
        var span = fileAnchor.ToSpan();
        if (string.IsNullOrWhiteSpace(span.File))
            throw new InvalidOperationException("Modules.Import.Into(Anchor) needs File(path)");
        _into = span.File;
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        ModulesRunner.ApplyImportAsync(bus, plan, name, _into, ct);
}

internal static class ModulesRunner
{
    public const string Kind = "modules.import";

    public static Task<StepResponse> ApplyImportAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string name,
        string? into,
        CancellationToken ct)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(StepResponse.Fail(Kind, "Modules.Import(name) required"));
        if (string.IsNullOrWhiteSpace(into))
            return Task.FromResult(StepResponse.Fail(Kind, "Into(file) required"));

        var file = plan.Resolve(into!);
        var lang = (plan.Language ?? InferLang(file)).Trim().ToLowerInvariant();
        if (!File.Exists(file))
            return Task.FromResult(StepResponse.Fail(Kind, "file missing — TypeDecl.Create first", new { path = file }));

        var text = File.ReadAllText(file);
        if (AlreadyImported(lang, text, name.Trim()))
        {
            var skip = StepResponse.Success(Kind, "already:" + name.Trim(), new { path = file, language = lang });
            bus.RecordLocal("modules", Kind, ScriptArgs.From(new { name, path = file }), skip.ToJson());
            return Task.FromResult(skip);
        }

        var line = ProjectImport(lang, name.Trim());
        var newText = InsertImport(lang, text, line);
        var args = ScriptArgs.From(new { name = name.Trim(), path = file, language = lang, line });
        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(Kind, "dry_run", new { dry_run = true, path = file, line });
            bus.RecordLocal("modules", Kind, args, dry.ToJson(), skippedDryRun: true);
            return Task.FromResult(dry);
        }

        File.WriteAllText(file, newText);
        var ok = StepResponse.Success(Kind, "imported:" + name.Trim(), new { path = file, language = lang, line });
        bus.RecordLocal("modules", Kind, args, ok.ToJson());
        return Task.FromResult(ok);
    }

    private static string InferLang(string file) =>
        file.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ? "python"
        : file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
            ? "typescript"
            : "csharp";

    private static string ProjectImport(string language, string name) => language switch
    {
        "python" => name.Contains('.', StringComparison.Ordinal) || name.Contains(' ', StringComparison.Ordinal)
            ? "import " + name
            : "import " + name,
        "typescript" => $"import \"{name}\";",
        _ => $"using {name};"
    };

    private static bool AlreadyImported(string language, string text, string name)
    {
        var needle = language switch
        {
            "python" => "import " + name,
            "typescript" => $"import \"{name}\"",
            _ => $"using {name};"
        };
        return text.Contains(needle, StringComparison.Ordinal);
    }

    private static string InsertImport(string language, string text, string line)
    {
        if (language == "python")
        {
            // after shebang / encoding / existing imports
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
            var i = 0;
            while (i < lines.Count && (lines[i].StartsWith('#') || string.IsNullOrWhiteSpace(lines[i])
                                       || lines[i].StartsWith("import ", StringComparison.Ordinal)
                                       || lines[i].StartsWith("from ", StringComparison.Ordinal)))
                i++;
            lines.Insert(i, line);
            return string.Join("\n", lines);
        }

        if (language == "typescript")
        {
            return line + "\n" + text;
        }

        // csharp: before namespace / file-scoped namespace; after existing usings
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var parts = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var insertAt = 0;
        while (insertAt < parts.Count)
        {
            var t = parts[insertAt].TrimStart();
            if (t.StartsWith("using ", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(t)
                || t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("#", StringComparison.Ordinal))
            {
                insertAt++;
                continue;
            }

            break;
        }

        parts.Insert(insertAt, line);
        return string.Join(nl, parts);
    }
}
