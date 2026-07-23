using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cdp.ScriptableIde;

/// <summary>Create a method on an existing type (Anchor M:TypeName).</summary>
public sealed class MethodFacade(IScriptToolBus bus, PlanContext plan)
{
    public MethodBuilder Create(string name) => new(bus, plan, name);
}

public sealed class MethodBuilder(IScriptToolBus bus, PlanContext plan, string name)
{
    private string? _typeAnchor;
    private TypeIntent? _returns;
    private bool _static;
    private readonly List<(string Name, TypeIntent Type)> _params = [];

    public MethodBuilder In(string typeAnchor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeAnchor);
        _typeAnchor = typeAnchor.Trim();
        return this;
    }

    public MethodBuilder In(Anchor typeAnchor) => In(typeAnchor.ToWire());

    public MethodBuilder Static(bool value = true)
    {
        _static = value;
        return this;
    }

    public MethodBuilder Returns(TypeIntent type)
    {
        ArgumentNullException.ThrowIfNull(type);
        _returns = type;
        return this;
    }

    public MethodBuilder Param(string paramName, TypeIntent type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paramName);
        ArgumentNullException.ThrowIfNull(type);
        _params.Add((paramName.Trim(), type));
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        MethodCreateRunner.ApplyAsync(bus, plan, name, _typeAnchor, _returns, _static, _params, ct);
}

internal static class MethodCreateRunner
{
    public const string Kind = "method.create";

    public static Task<StepResponse> ApplyAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string name,
        string? typeAnchor,
        TypeIntent? returns,
        bool isStatic,
        IReadOnlyList<(string Name, TypeIntent Type)> parameters,
        CancellationToken ct)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(StepResponse.Fail(Kind, "Method.Create(name) required"));
        if (string.IsNullOrWhiteSpace(typeAnchor))
            return Task.FromResult(StepResponse.Fail(Kind, "In(typeAnchor) required"));
        if (!IsIdent(name))
            return Task.FromResult(StepResponse.Fail(Kind, "method name must be an identifier"));

        var lang = (plan.Language ?? "csharp").Trim().ToLowerInvariant();
        if (lang is not "csharp")
            return Task.FromResult(StepResponse.Fail(Kind, "Method.Create csharp-only v1"));

        if (!AnchorLocus.TryResolveFile(plan, typeAnchor!, Kind, out var file, out var span, out var fail))
            return Task.FromResult(fail!);
        if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(StepResponse.Fail(Kind, "csharp only (.cs)", new { file }));

        if (!TryFindType(file, span, out var typeDecl, out var typeName, out var detail))
            return Task.FromResult(StepResponse.Fail(Kind, "type locate failed: " + detail, new { anchor = typeAnchor }));

        if (typeDecl.Members.OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text.Equals(name, StringComparison.Ordinal)
                      && m.ParameterList.Parameters.Count == parameters.Count))
        {
            var skip = StepResponse.Success(Kind, "already:" + name, new { path = file, type = typeName });
            bus.RecordLocal("method", Kind, ScriptArgs.From(new { name, path = file }), skip.ToJson());
            return Task.FromResult(skip);
        }

        string retWire;
        if (returns is null || returns is InferTypeIntent)
            retWire = "void";
        else if (!TypeProjection.TryProject(lang, returns, out retWire!, out var terr))
            return Task.FromResult(StepResponse.Fail(Kind, terr ?? "returns project failed"));

        var paramParts = new List<string>();
        foreach (var (pn, pt) in parameters)
        {
            if (!IsIdent(pn))
                return Task.FromResult(StepResponse.Fail(Kind, "param name must be identifier: " + pn));
            if (!TypeProjection.TryProject(lang, pt, out var tw, out var perr))
                return Task.FromResult(StepResponse.Fail(Kind, perr ?? "param type project failed"));
            paramParts.Add($"{tw} {pn}");
        }

        var mods = isStatic ? "public static" : "public";
        var sig = $"{mods} {retWire} {name}({string.Join(", ", paramParts)})";
        var methodText = $"\n    {sig}\n    {{\n    }}\n";

        var text = File.ReadAllText(file);
        var close = typeDecl.CloseBraceToken.Span.Start;
        var before = text[..close];
        var gap = before.EndsWith('\n') ? "" : "\n";
        var newText = before + gap + methodText + text[close..];

        var args = ScriptArgs.From(new { name, path = file, type = typeName, static_member = isStatic, returns = retWire });
        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(Kind, "dry_run", new { dry_run = true, path = file, preview = methodText });
            bus.RecordLocal("method", Kind, args, dry.ToJson(), skippedDryRun: true);
            return Task.FromResult(dry);
        }

        File.WriteAllText(file, newText);
        var ok = StepResponse.Success(Kind, "created:" + name, new { path = file, type = typeName, method = name });
        bus.RecordLocal("method", Kind, args, ok.ToJson());
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
