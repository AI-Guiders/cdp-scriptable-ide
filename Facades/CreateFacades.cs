using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cdp.ScriptableIde;

/// <summary>
/// Declare family: <c>Create.Record|Class|Method|Property|Field</c> — Anchors-out; not Refactor.At clone.
/// </summary>
public sealed class CreateFacade(IScriptToolBus bus, PlanContext plan)
{
    public CreateTypeBuilder Record(string name) => new(bus, plan, name, CreateTypeKind.Record);
    public CreateTypeBuilder Class(string name) => new(bus, plan, name, CreateTypeKind.Class);

    public CreateMethodBuilder Method(string name) => new(bus, plan, name);
    public CreatePropertyBuilder Property(string name) => new(bus, plan, name);
    public CreateFieldBuilder Field(string name) => new(bus, plan, name);
}

public enum CreateTypeKind
{
    Class,
    Record
}

public sealed class CreateTypeBuilder(IScriptToolBus bus, PlanContext plan, string name, CreateTypeKind kind)
{
    private string? _into;
    private string? _ns;
    private bool _replace;
    private bool? _final;
    private bool _abstract;
    private AccessIntent? _access;
    private readonly List<FieldSpec> _fields = [];

    public CreateTypeBuilder With(FieldSpec field)
    {
        ArgumentNullException.ThrowIfNull(field);
        _fields.Add(field);
        return this;
    }

    public CreateTypeBuilder Final(bool value = true)
    {
        _final = value;
        return this;
    }

    public CreateTypeBuilder Abstract(bool value = true)
    {
        _abstract = value;
        return this;
    }

    public CreateTypeBuilder Public() => Access(AccessIntent.Public);
    public CreateTypeBuilder Private() => Access(AccessIntent.Private);
    public CreateTypeBuilder Protected() => Access(AccessIntent.Protected);
    public CreateTypeBuilder Internal() => Access(AccessIntent.Internal);

    public CreateTypeBuilder Access(AccessIntent access)
    {
        if (_access is { } prev && prev != access)
            throw new InvalidOperationException($"Create access conflict: {prev} then {access}");
        _access = access;
        return this;
    }

    public CreateTypeBuilder Namespace(string ns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        _ns = ns.Trim();
        return this;
    }

    public CreateTypeBuilder Into(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _into = filePath.Trim();
        return this;
    }

    public CreateTypeBuilder Replace(bool value = true)
    {
        _replace = value;
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        CreateTypeRunner.ApplyAsync(
            bus, plan, name, kind, _into, _ns, _replace, _final, _abstract, _access ?? AccessIntent.Public, _fields, ct);
}

internal static class CreateTypeRunner
{
    public const string Kind = "create.type";

    public static Task<StepResponse> ApplyAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string name,
        CreateTypeKind kind,
        string? into,
        string? ns,
        bool replace,
        bool? final,
        bool isAbstract,
        AccessIntent access,
        IReadOnlyList<FieldSpec> fields,
        CancellationToken ct)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(StepResponse.Fail(Kind, "Create.Record|Class(name) required"));
        if (string.IsNullOrWhiteSpace(into))
            return Task.FromResult(StepResponse.Fail(Kind, "Into(file) required"));
        if (!CsharpIdents.IsIdent(name))
            return Task.FromResult(StepResponse.Fail(Kind, "type name must be an identifier"));

        var lang = (plan.Language ?? "csharp").Trim().ToLowerInvariant();
        if (lang is not "csharp")
            return Task.FromResult(StepResponse.Fail(Kind, "Create type csharp-only v1", new { language = lang }));

        if (isAbstract && final is true)
            return Task.FromResult(StepResponse.Fail(Kind, "Final and Abstract are mutually exclusive"));

        var impliedFinal = kind == CreateTypeKind.Record && final is not false;
        var useFinal = final is true || impliedFinal;
        if (isAbstract && kind == CreateTypeKind.Record)
            return Task.FromResult(StepResponse.Fail(Kind, "Abstract record unsupported in Create v1"));

        if (!AccessProjection.TryProject(lang, access, topLevelType: true, out var accessWire, out var aerr))
            return Task.FromResult(StepResponse.Fail(Kind, aerr ?? "access project failed"));

        var file = plan.Resolve(into!);
        var fieldWires = new List<(string Name, string Type)>();
        foreach (var f in fields)
        {
            if (!CsharpIdents.IsIdent(f.Name))
                return Task.FromResult(StepResponse.Fail(Kind, "field name must be identifier: " + f.Name));
            if (!TypeProjection.TryProject(lang, f.Type, out var tw, out var terr))
                return Task.FromResult(StepResponse.Fail(Kind, terr ?? "field type project failed"));
            fieldWires.Add((f.Name, tw!));
        }

        var typeText = ProjectType(kind, name, accessWire, useFinal, isAbstract, fieldWires);
        var body = string.IsNullOrWhiteSpace(ns) ? typeText + "\n" : $"namespace {ns};\n\n{typeText}\n";
        var anchor = Anchor.File(file).Member(name).ToWire();
        var args = ScriptArgs.From(new { name, path = file, kind = kind.ToString().ToLowerInvariant(), ns, replace, access = accessWire });

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(Kind, "dry_run", new { dry_run = true, path = file, name, anchor, preview = body });
            bus.RecordLocal("create", Kind, args, dry.ToJson(), skippedDryRun: true);
            return Task.FromResult(dry);
        }

        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (replace || !File.Exists(file))
        {
            var existed = File.Exists(file);
            File.WriteAllText(file, body);
            var verb = replace && existed ? "replaced:" : "created:";
            var created = StepResponse.Success(Kind, verb + name, new
            {
                path = file,
                name,
                kind = kind.ToString().ToLowerInvariant(),
                replace,
                anchor
            });
            bus.RecordLocal("create", Kind, args, created.ToJson());
            return Task.FromResult(created);
        }

        var existing = File.ReadAllText(file);
        if (existing.Contains($"class {name}", StringComparison.Ordinal)
            || existing.Contains($"record {name}", StringComparison.Ordinal))
        {
            var skip = StepResponse.Success(Kind, "already:" + name, new { path = file, name, anchor });
            bus.RecordLocal("create", Kind, args, skip.ToJson());
            return Task.FromResult(skip);
        }

        var newText = existing.TrimEnd() + "\n\n" + typeText + "\n";
        File.WriteAllText(file, newText);
        var ok = StepResponse.Success(Kind, "created:" + name, new
        {
            path = file,
            name,
            kind = kind.ToString().ToLowerInvariant(),
            anchor
        });
        bus.RecordLocal("create", Kind, args, ok.ToJson());
        return Task.FromResult(ok);
    }

    private static string ProjectType(
        CreateTypeKind kind,
        string name,
        string access,
        bool useFinal,
        bool isAbstract,
        IReadOnlyList<(string Name, string Type)> fields)
    {
        var mods = access;
        if (isAbstract)
            mods += " abstract";
        else if (useFinal)
            mods += " sealed";

        if (kind == CreateTypeKind.Record)
        {
            if (fields.Count == 0)
                return $"{mods} record {name};\n";
            var parts = fields.Select(f => $"{f.Type} {f.Name}");
            return $"{mods} record {name}({string.Join(", ", parts)});\n";
        }

        // class
        if (fields.Count == 0)
            return $"{mods} class {name}\n{{\n}}\n";

        var props = string.Join("\n", fields.Select(f => $"    public {f.Type} {f.Name} {{ get; set; }}\n"));
        return $"{mods} class {name}\n{{\n{props}}}\n";
    }
}

public sealed class CreateMethodBuilder(IScriptToolBus bus, PlanContext plan, string name)
{
    private string? _typeAnchor;
    private TypeIntent? _returns;
    private bool _static;
    private AccessIntent? _access;
    private readonly List<(string Name, TypeIntent Type)> _params = [];

    public CreateMethodBuilder In(string typeAnchor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeAnchor);
        _typeAnchor = typeAnchor.Trim();
        return this;
    }

    public CreateMethodBuilder In(Anchor typeAnchor) => In(typeAnchor.ToWire());

    public CreateMethodBuilder Static(bool value = true)
    {
        _static = value;
        return this;
    }

    public CreateMethodBuilder Public() => Access(AccessIntent.Public);
    public CreateMethodBuilder Private() => Access(AccessIntent.Private);
    public CreateMethodBuilder Protected() => Access(AccessIntent.Protected);
    public CreateMethodBuilder Internal() => Access(AccessIntent.Internal);

    public CreateMethodBuilder Access(AccessIntent access)
    {
        if (_access is { } prev && prev != access)
            throw new InvalidOperationException($"Create.Method access conflict: {prev} then {access}");
        _access = access;
        return this;
    }

    public CreateMethodBuilder Returns(TypeIntent type)
    {
        ArgumentNullException.ThrowIfNull(type);
        _returns = type;
        return this;
    }

    public CreateMethodBuilder Param(string paramName, TypeIntent type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paramName);
        ArgumentNullException.ThrowIfNull(type);
        _params.Add((paramName.Trim(), type));
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        CreateMemberRunner.ApplyMethodAsync(
            bus, plan, name, _typeAnchor, _returns, _static, _access ?? AccessIntent.Public, _params, ct);
}

public sealed class CreatePropertyBuilder(IScriptToolBus bus, PlanContext plan, string name)
{
    private string? _typeAnchor;
    private TypeIntent? _of;
    private AccessIntent? _access;

    public CreatePropertyBuilder In(string typeAnchor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeAnchor);
        _typeAnchor = typeAnchor.Trim();
        return this;
    }

    public CreatePropertyBuilder In(Anchor typeAnchor) => In(typeAnchor.ToWire());

    public CreatePropertyBuilder Of(TypeIntent type)
    {
        ArgumentNullException.ThrowIfNull(type);
        _of = type;
        return this;
    }

    public CreatePropertyBuilder Public() => Access(AccessIntent.Public);
    public CreatePropertyBuilder Private() => Access(AccessIntent.Private);
    public CreatePropertyBuilder Protected() => Access(AccessIntent.Protected);
    public CreatePropertyBuilder Internal() => Access(AccessIntent.Internal);

    public CreatePropertyBuilder Access(AccessIntent access)
    {
        if (_access is { } prev && prev != access)
            throw new InvalidOperationException($"Create.Property access conflict: {prev} then {access}");
        _access = access;
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        CreateMemberRunner.ApplyPropertyAsync(bus, plan, name, _typeAnchor, _of, _access ?? AccessIntent.Public, ct);
}

public sealed class CreateFieldBuilder(IScriptToolBus bus, PlanContext plan, string name)
{
    private string? _typeAnchor;
    private TypeIntent? _of;
    private AccessIntent? _access;

    public CreateFieldBuilder In(string typeAnchor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeAnchor);
        _typeAnchor = typeAnchor.Trim();
        return this;
    }

    public CreateFieldBuilder In(Anchor typeAnchor) => In(typeAnchor.ToWire());

    public CreateFieldBuilder Of(TypeIntent type)
    {
        ArgumentNullException.ThrowIfNull(type);
        _of = type;
        return this;
    }

    public CreateFieldBuilder Public() => Access(AccessIntent.Public);
    public CreateFieldBuilder Private() => Access(AccessIntent.Private);
    public CreateFieldBuilder Protected() => Access(AccessIntent.Protected);
    public CreateFieldBuilder Internal() => Access(AccessIntent.Internal);

    public CreateFieldBuilder Access(AccessIntent access)
    {
        if (_access is { } prev && prev != access)
            throw new InvalidOperationException($"Create.Field access conflict: {prev} then {access}");
        _access = access;
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        CreateMemberRunner.ApplyFieldAsync(bus, plan, name, _typeAnchor, _of, _access ?? AccessIntent.Private, ct);
}

internal static class CreateMemberRunner
{
    public const string MethodKind = "create.method";
    public const string PropertyKind = "create.property";
    public const string FieldKind = "create.field";

    public static Task<StepResponse> ApplyMethodAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string name,
        string? typeAnchor,
        TypeIntent? returns,
        bool isStatic,
        AccessIntent access,
        IReadOnlyList<(string Name, TypeIntent Type)> parameters,
        CancellationToken ct)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(name) || !CsharpIdents.IsIdent(name))
            return Task.FromResult(StepResponse.Fail(MethodKind, "Create.Method(name) identifier required"));
        if (string.IsNullOrWhiteSpace(typeAnchor))
            return Task.FromResult(StepResponse.Fail(MethodKind, "In(typeAnchor) required"));

        var lang = (plan.Language ?? "csharp").Trim().ToLowerInvariant();
        if (lang is not "csharp")
            return Task.FromResult(StepResponse.Fail(MethodKind, "csharp-only v1"));

        if (!AccessProjection.TryProject(lang, access, topLevelType: false, out var accessWire, out var aerr))
            return Task.FromResult(StepResponse.Fail(MethodKind, aerr ?? "access failed"));

        if (!AnchorLocus.TryResolveFile(plan, typeAnchor!, MethodKind, out var file, out var span, out var fail))
            return Task.FromResult(fail!);
        if (!TryFindType(file, span, out var typeDecl, out var typeName, out var detail))
            return Task.FromResult(StepResponse.Fail(MethodKind, "type locate failed: " + detail));

        if (typeDecl.Members.OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text.Equals(name, StringComparison.Ordinal)
                      && m.ParameterList.Parameters.Count == parameters.Count))
        {
            var a = Anchor.File(file).Method(name).ToWire();
            return Task.FromResult(StepResponse.Success(MethodKind, "already:" + name, new { path = file, type = typeName, method = name, anchor = a }));
        }

        string retWire;
        if (returns is null || returns is InferTypeIntent)
            retWire = "void";
        else if (!TypeProjection.TryProject(lang, returns, out retWire!, out var terr))
            return Task.FromResult(StepResponse.Fail(MethodKind, terr ?? "returns failed"));

        var paramParts = new List<string>();
        foreach (var (pn, pt) in parameters)
        {
            if (!CsharpIdents.IsIdent(pn))
                return Task.FromResult(StepResponse.Fail(MethodKind, "bad param: " + pn));
            if (!TypeProjection.TryProject(lang, pt, out var tw, out var perr))
                return Task.FromResult(StepResponse.Fail(MethodKind, perr ?? "param type failed"));
            paramParts.Add($"{tw} {pn}");
        }

        var mods = accessWire + (isStatic ? " static" : "");
        var methodText = $"\n    {mods} {retWire} {name}({string.Join(", ", paramParts)})\n    {{\n    }}\n";
        return InsertMemberAsync(bus, plan, MethodKind, file, typeDecl, typeName, name, methodText, "method");
    }

    public static Task<StepResponse> ApplyPropertyAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string name,
        string? typeAnchor,
        TypeIntent? of,
        AccessIntent access,
        CancellationToken ct)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(name) || !CsharpIdents.IsIdent(name))
            return Task.FromResult(StepResponse.Fail(PropertyKind, "Create.Property(name) identifier required"));
        if (string.IsNullOrWhiteSpace(typeAnchor))
            return Task.FromResult(StepResponse.Fail(PropertyKind, "In(typeAnchor) required"));
        if (of is null)
            return Task.FromResult(StepResponse.Fail(PropertyKind, "Of(type) required"));

        var lang = (plan.Language ?? "csharp").Trim().ToLowerInvariant();
        if (lang is not "csharp")
            return Task.FromResult(StepResponse.Fail(PropertyKind, "csharp-only v1"));
        if (!AccessProjection.TryProject(lang, access, topLevelType: false, out var accessWire, out var aerr))
            return Task.FromResult(StepResponse.Fail(PropertyKind, aerr ?? "access failed"));
        if (!TypeProjection.TryProject(lang, of, out var tw, out var terr))
            return Task.FromResult(StepResponse.Fail(PropertyKind, terr ?? "type failed"));

        if (!AnchorLocus.TryResolveFile(plan, typeAnchor!, PropertyKind, out var file, out var span, out var fail))
            return Task.FromResult(fail!);
        if (!TryFindType(file, span, out var typeDecl, out var typeName, out var detail))
            return Task.FromResult(StepResponse.Fail(PropertyKind, "type locate failed: " + detail));

        if (typeDecl.Members.OfType<PropertyDeclarationSyntax>()
            .Any(p => p.Identifier.Text.Equals(name, StringComparison.Ordinal)))
        {
            var a = Anchor.File(file).Member(name).ToWire();
            return Task.FromResult(StepResponse.Success(PropertyKind, "already:" + name, new { path = file, type = typeName, property = name, anchor = a }));
        }

        var propText = $"\n    {accessWire} {tw} {name} {{ get; set; }}\n";
        return InsertMemberAsync(bus, plan, PropertyKind, file, typeDecl, typeName, name, propText, "property");
    }

    public static Task<StepResponse> ApplyFieldAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string name,
        string? typeAnchor,
        TypeIntent? of,
        AccessIntent access,
        CancellationToken ct)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(name) || !CsharpIdents.IsIdent(name))
            return Task.FromResult(StepResponse.Fail(FieldKind, "Create.Field(name) identifier required"));
        if (string.IsNullOrWhiteSpace(typeAnchor))
            return Task.FromResult(StepResponse.Fail(FieldKind, "In(typeAnchor) required"));
        if (of is null)
            return Task.FromResult(StepResponse.Fail(FieldKind, "Of(type) required"));

        var lang = (plan.Language ?? "csharp").Trim().ToLowerInvariant();
        if (lang is not "csharp")
            return Task.FromResult(StepResponse.Fail(FieldKind, "csharp-only v1"));
        if (!AccessProjection.TryProject(lang, access, topLevelType: false, out var accessWire, out var aerr))
            return Task.FromResult(StepResponse.Fail(FieldKind, aerr ?? "access failed"));
        if (!TypeProjection.TryProject(lang, of, out var tw, out var terr))
            return Task.FromResult(StepResponse.Fail(FieldKind, terr ?? "type failed"));

        if (!AnchorLocus.TryResolveFile(plan, typeAnchor!, FieldKind, out var file, out var span, out var fail))
            return Task.FromResult(fail!);
        if (!TryFindType(file, span, out var typeDecl, out var typeName, out var detail))
            return Task.FromResult(StepResponse.Fail(FieldKind, "type locate failed: " + detail));

        if (typeDecl.Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .Any(v => v.Identifier.Text.Equals(name, StringComparison.Ordinal)))
        {
            var a = Anchor.File(file).Member(name).ToWire();
            return Task.FromResult(StepResponse.Success(FieldKind, "already:" + name, new { path = file, type = typeName, field = name, anchor = a }));
        }

        var fieldText = $"\n    {accessWire} {tw} {name};\n";
        return InsertMemberAsync(bus, plan, FieldKind, file, typeDecl, typeName, name, fieldText, "field");
    }

    private static Task<StepResponse> InsertMemberAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string kind,
        string file,
        TypeDeclarationSyntax typeDecl,
        string typeName,
        string memberName,
        string memberText,
        string memberKind)
    {
        var text = File.ReadAllText(file);
        var close = typeDecl.CloseBraceToken.Span.Start;
        var before = text[..close];
        var gap = before.EndsWith('\n') ? "" : "\n";
        var newText = before + gap + memberText + text[close..];
        var anchor = Anchor.File(file).Member(memberName).ToWire();
        var args = ScriptArgs.From(new { name = memberName, path = file, type = typeName });

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(kind, "dry_run", new { dry_run = true, path = file, preview = memberText, anchor });
            bus.RecordLocal("create", kind, args, dry.ToJson(), skippedDryRun: true);
            return Task.FromResult(dry);
        }

        File.WriteAllText(file, newText);
        var ok = StepResponse.Success(kind, "created:" + memberName, new
        {
            path = file,
            type = typeName,
            memberKind,
            name = memberName,
            anchor
        });
        bus.RecordLocal("create", kind, args, ok.ToJson());
        return Task.FromResult(ok);
    }

    private static bool TryFindType(
        string file,
        BracketLocate.Span span,
        out TypeDeclarationSyntax type,
        out string typeName,
        out string detail)
    {
        type = null!;
        typeName = "";
        detail = "";
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
        var root = tree.GetCompilationUnitRoot();
        if (!string.IsNullOrWhiteSpace(span.MemberKey))
        {
            var hit = root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(t => t.Identifier.Text.Equals(span.MemberKey, StringComparison.Ordinal));
            if (hit is null)
            {
                detail = "type_not_found";
                return false;
            }

            type = hit;
            typeName = hit.Identifier.Text;
            detail = "member";
            return true;
        }

        detail = "need_M_TypeName";
        return false;
    }
}
