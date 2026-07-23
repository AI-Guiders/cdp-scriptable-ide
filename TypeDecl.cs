namespace Cdp.ScriptableIde;

/// <summary>Create a type shell in a file (csharp class / …).</summary>
public sealed class TypeDeclFacade(IScriptToolBus bus, PlanContext plan)
{
    public TypeDeclBuilder Create(string name) => new(bus, plan, name);
}

public sealed class TypeDeclBuilder(IScriptToolBus bus, PlanContext plan, string name)
{
    private string? _into;
    private string? _ns;
    private bool _static;

    private bool _replace;

    public TypeDeclBuilder Static(bool value = true)
    {
        _static = value;
        return this;
    }

    public TypeDeclBuilder Namespace(string ns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        _ns = ns.Trim();
        return this;
    }

    public TypeDeclBuilder Into(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _into = filePath.Trim();
        return this;
    }

    /// <summary>Bootstrap: overwrite file with a fresh type shell (drops prior body/imports).</summary>
    public TypeDeclBuilder Replace(bool value = true)
    {
        _replace = value;
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        TypeDeclRunner.ApplyAsync(bus, plan, name, _into, _ns, _static, _replace, ct);
}

internal static class TypeDeclRunner
{
    public const string Kind = "typedecl.create";

    public static Task<StepResponse> ApplyAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string name,
        string? into,
        string? ns,
        bool isStatic,
        bool replace,
        CancellationToken ct)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(StepResponse.Fail(Kind, "TypeDecl.Create(name) required"));
        if (string.IsNullOrWhiteSpace(into))
            return Task.FromResult(StepResponse.Fail(Kind, "Into(file) required"));
        if (!IsIdent(name))
            return Task.FromResult(StepResponse.Fail(Kind, "type name must be an identifier"));

        var file = plan.Resolve(into!);
        var lang = (plan.Language ?? "csharp").Trim().ToLowerInvariant();
        if (lang is not "csharp")
            return Task.FromResult(StepResponse.Fail(Kind, "TypeDecl.Create csharp-only v1", new { language = lang }));

        var args = ScriptArgs.From(new { name, path = file, ns, is_static = isStatic, replace });
        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(Kind, "dry_run", new { dry_run = true, path = file, name, replace });
            bus.RecordLocal("typedecl", Kind, args, dry.ToJson(), skippedDryRun: true);
            return Task.FromResult(dry);
        }

        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (replace || !File.Exists(file))
        {
            var existed = File.Exists(file);
            var body = ProjectFile(ns, name, isStatic);
            File.WriteAllText(file, body);
            var verb = replace && existed ? "replaced:" : "created:";
            var created = StepResponse.Success(Kind, verb + name, new { path = file, name, replace });
            bus.RecordLocal("typedecl", Kind, args, created.ToJson());
            return Task.FromResult(created);
        }

        var existing = File.ReadAllText(file);
        if (existing.Contains($"class {name}", StringComparison.Ordinal)
            || existing.Contains($"record {name}", StringComparison.Ordinal)
            || existing.Contains($"struct {name}", StringComparison.Ordinal))
        {
            var skip = StepResponse.Success(Kind, "already:" + name, new { path = file });
            bus.RecordLocal("typedecl", Kind, args, skip.ToJson());
            return Task.FromResult(skip);
        }

        var append = ProjectType(name, isStatic);
        var newText = existing.TrimEnd() + "\n\n" + append + "\n";
        File.WriteAllText(file, newText);

        var ok = StepResponse.Success(Kind, "created:" + name, new { path = file, name });
        bus.RecordLocal("typedecl", Kind, args, ok.ToJson());
        return Task.FromResult(ok);
    }

    private static string ProjectFile(string? ns, string name, bool isStatic)
    {
        var type = ProjectType(name, isStatic);
        if (string.IsNullOrWhiteSpace(ns))
            return type + "\n";
        return $"namespace {ns};\n\n{type}\n";
    }

    private static string ProjectType(string name, bool isStatic)
    {
        var mod = isStatic ? "public static class" : "public class";
        return $"{mod} {name}\n{{\n}}\n";
    }

    private static bool IsIdent(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return false;
        if (!(char.IsLetter(s[0]) || s[0] == '_'))
            return false;
        for (var i = 1; i < s.Length; i++)
        {
            if (!(char.IsLetterOrDigit(s[i]) || s[i] == '_'))
                return false;
        }

        return true;
    }
}
