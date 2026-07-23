using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cdp.ScriptableIde;

/// <summary>Form change: <c>Convert.ToProperty.At(fieldAnchor)</c> / <c>Convert.AnonymousReturn.At(method).To(T)</c>.</summary>
public sealed class ConvertFacade(IScriptToolBus bus, PlanContext plan)
{
    public ConvertToPropertyBuilder ToProperty => new(bus, plan);

    /// <summary>
    /// <c>Convert.AnonymousReturn.At(method).To(Types.Of("X"))</c> —
    /// <c>return new { a = e1, b = e2 };</c> → <c>return new X(a: e1, b: e2);</c> inside the method.
    /// </summary>
    public ConvertAnonymousReturnBuilder AnonymousReturn => new(bus, plan);
}

public sealed class ConvertToPropertyBuilder(IScriptToolBus bus, PlanContext plan)
{
    public ConvertToPropertyAt At(string anchorTarget) => new(bus, plan, anchorTarget);
    public ConvertToPropertyAt At(Anchor anchor) => At(anchor.ToWire());
    public ConvertToPropertyAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class ConvertToPropertyAt(IScriptToolBus bus, PlanContext plan, string anchorTarget)
{
    private string? _name;

    /// <summary>Optional property name (default: field name with leading _ / m_ stripped).</summary>
    public ConvertToPropertyAt Name(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        _name = propertyName.Trim();
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        ConvertToPropertyRunner.RunAsync(bus, plan, anchorTarget, _name, ct);
}

internal static class ConvertToPropertyRunner
{
    public const string Kind = "convert.to_property";

    public static Task<StepResponse> RunAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string anchorTarget,
        string? propertyName,
        CancellationToken ct)
    {
        _ = ct;
        if (!AnchorLocus.TryResolveFile(plan, anchorTarget, Kind, out var file, out var span, out var fail))
            return Task.FromResult(fail!);
        if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(StepResponse.Fail(Kind, "csharp only (.cs)"));

        var text = File.ReadAllText(file);
        var tree = CSharpSyntaxTree.ParseText(text);
        var root = tree.GetCompilationUnitRoot();

        SyntaxNode? focus = null;
        string detail;
        if (BracketSyntaxResolve.TryFindAttachTarget(file, span, out var target, out detail))
            focus = target.Node;
        else if (!string.IsNullOrWhiteSpace(span.MemberKey))
        {
            focus = root.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Identifier.Text.Equals(span.MemberKey, StringComparison.Ordinal)
                                     && v.Parent?.Parent is FieldDeclarationSyntax);
            if (focus is null)
                return Task.FromResult(StepResponse.Fail(Kind, "field locate failed: " + detail, new { anchor = anchorTarget }));
        }
        else
        {
            return Task.FromResult(StepResponse.Fail(Kind, "locate failed: " + detail, new { anchor = anchorTarget }));
        }

        var decl = focus switch
        {
            VariableDeclaratorSyntax v when v.Parent?.Parent is FieldDeclarationSyntax f => (v, f),
            FieldDeclarationSyntax f when f.Declaration.Variables.Count == 1 => (f.Declaration.Variables[0], f),
            _ => default((VariableDeclaratorSyntax v, FieldDeclarationSyntax f)?)
        };
        if (decl is null)
            return Task.FromResult(StepResponse.Fail(Kind, "Convert.ToProperty requires a field locus", new
            {
                node = focus.Kind().ToString()
            }));

        var (variable, field) = decl.Value;
        if (field.Declaration.Variables.Count != 1)
            return Task.FromResult(StepResponse.Fail(Kind, "multi-variable field declarations unsupported"));

        var fieldName = variable.Identifier.Text;
        var propName = propertyName ?? StripFieldPrefix(fieldName);
        if (!CsharpIdents.IsIdent(propName))
            return Task.FromResult(StepResponse.Fail(Kind, "property name must be identifier: " + propName));

        var typeWire = field.Declaration.Type.ToString();
        var access = field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)) ? "public"
            : field.Modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword)) ? "protected"
            : field.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)) ? "internal"
            : field.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)) ? "private"
            : "public";

        var propText = $"    {access} {typeWire} {propName} {{ get; set; }}\n";
        var fieldSpan = field.FullSpan;
        var newText = text[..fieldSpan.Start] + propText + text[fieldSpan.End..];
        var anchor = Anchor.File(file).Member(propName).ToWire();
        var args = ScriptArgs.From(new { path = file, field = fieldName, property = propName });

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(Kind, "dry_run", new { dry_run = true, path = file, preview = propText, anchor });
            bus.RecordLocal("convert", Kind, args, dry.ToJson(), skippedDryRun: true);
            return Task.FromResult(dry);
        }

        File.WriteAllText(file, newText);
        var ok = StepResponse.Success(Kind, $"field {fieldName} → property {propName}", new
        {
            path = file,
            field = fieldName,
            property = propName,
            anchor
        });
        bus.RecordLocal("convert", Kind, args, ok.ToJson());
        return Task.FromResult(ok);
    }

    private static string StripFieldPrefix(string fieldName)
    {
        if (fieldName.StartsWith("m_", StringComparison.Ordinal) && fieldName.Length > 2)
            return char.ToUpperInvariant(fieldName[2]) + fieldName[3..];
        if (fieldName.StartsWith('_') && fieldName.Length > 1)
            return char.ToUpperInvariant(fieldName[1]) + fieldName[2..];
        return fieldName;
    }
}

public sealed class ConvertAnonymousReturnBuilder(IScriptToolBus bus, PlanContext plan)
{
    public ConvertAnonymousReturnAt At(string anchorTarget) => new(bus, plan, anchorTarget);
    public ConvertAnonymousReturnAt At(Anchor anchor) => At(anchor.ToWire());
    public ConvertAnonymousReturnAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class ConvertAnonymousReturnAt(IScriptToolBus bus, PlanContext plan, string anchorTarget)
{
    public ConvertAnonymousReturnApply To(TypeIntent type) => new(bus, plan, anchorTarget, type);
    public ConvertAnonymousReturnApply To(Anchor typeAnchor)
    {
        var span = typeAnchor.ToSpan();
        var id = span.MemberKey;
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("To(Anchor) needs M:TypeName on the created type");
        return To(Types.Of(id!));
    }

    public ConvertAnonymousReturnApply To(string typeIdentifier) => To(Types.Of(typeIdentifier));
}

public sealed class ConvertAnonymousReturnApply(
    IScriptToolBus bus, PlanContext plan, string anchorTarget, TypeIntent type)
{
    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        ConvertAnonymousReturnRunner.RunAsync(bus, plan, anchorTarget, type, ct);
}

internal static class ConvertAnonymousReturnRunner
{
    public const string Kind = "convert.anonymous_return";

    public static Task<StepResponse> RunAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string anchorTarget,
        TypeIntent type,
        CancellationToken ct)
    {
        _ = ct;
        var lang = (plan.Language ?? "csharp").Trim().ToLowerInvariant();
        if (lang is not "csharp")
            return Task.FromResult(StepResponse.Fail(Kind, "csharp-only v1"));
        if (!TypeProjection.TryProject(lang, type, out var typeWire, out var terr))
            return Task.FromResult(StepResponse.Fail(Kind, terr ?? "type project failed"));

        if (!AnchorLocus.TryResolveFile(plan, anchorTarget, Kind, out var file, out var span, out var fail))
            return Task.FromResult(fail!);
        if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(StepResponse.Fail(Kind, "csharp only (.cs)"));

        var text = File.ReadAllText(file);
        var tree = CSharpSyntaxTree.ParseText(text);
        var root = tree.GetCompilationUnitRoot();

        MethodDeclarationSyntax? method = null;
        string detail = "";
        if (BracketSyntaxResolve.TryFindAttachTarget(file, span, out var target, out detail))
        {
            method = target.Node as MethodDeclarationSyntax
                     ?? target.Node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        }

        if (method is null && !string.IsNullOrWhiteSpace(span.MemberKey))
        {
            method = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text.Equals(span.MemberKey, StringComparison.Ordinal));
            detail = method is null ? "method not found: " + span.MemberKey : "member";
        }

        if (method is null)
            return Task.FromResult(StepResponse.Fail(Kind, "method locate failed: " + detail, new { anchor = anchorTarget }));

        var anonReturns = method.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Where(r => r.Expression is AnonymousObjectCreationExpressionSyntax)
            .OrderByDescending(r => r.SpanStart)
            .ToList();

        if (anonReturns.Count == 0)
            return Task.FromResult(StepResponse.Fail(Kind, "no anonymous object returns in method", new
            {
                method = method.Identifier.Text
            }));

        foreach (var ret in anonReturns)
        {
            var anon = (AnonymousObjectCreationExpressionSyntax)ret.Expression!;
            foreach (var init in anon.Initializers)
            {
                if (init.NameEquals?.Name.Identifier.Text is not { Length: > 0 })
                    return Task.FromResult(StepResponse.Fail(Kind, "anonymous member must be named (a = expr)", new
                    {
                        preview = init.ToString()
                    }));
            }
        }

        // Descending spans keep earlier indices valid while splicing.
        var newText = text;
        var rewritten = 0;
        foreach (var ret in anonReturns)
        {
            var anon = (AnonymousObjectCreationExpressionSyntax)ret.Expression!;
            var argParts = anon.Initializers.Select(init =>
            {
                var name = init.NameEquals!.Name.Identifier.Text;
                return $"{name}: {init.Expression}";
            });
            var replacement = $"return new {typeWire}({string.Join(", ", argParts)});";
            var s = ret.Span;
            newText = newText[..s.Start] + replacement + newText[s.End..];
            rewritten++;
        }

        var args = ScriptArgs.From(new
        {
            path = file,
            method = method.Identifier.Text,
            type = typeWire,
            count = rewritten
        });

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(Kind, "dry_run", new
            {
                dry_run = true,
                path = file,
                type = typeWire,
                count = rewritten
            });
            bus.RecordLocal("convert", Kind, args, dry.ToJson(), skippedDryRun: true);
            return Task.FromResult(dry);
        }

        File.WriteAllText(file, newText);
        var ok = StepResponse.Success(Kind, $"anonymous return → {typeWire} ×{rewritten}", new
        {
            path = file,
            method = method.Identifier.Text,
            type = typeWire,
            count = rewritten
        });
        bus.RecordLocal("convert", Kind, args, ok.ToJson());
        return Task.FromResult(ok);
    }
}

/// <summary><c>Refactor.Change.At(…ReturnType()).To(Types.Of(…))</c> — local TypeSyntax rewrite.</summary>
public sealed class RefactorChangeBuilder(IScriptToolBus bus, PlanContext plan)
{
    public RefactorChangeAt At(string anchorTarget) => new(bus, plan, anchorTarget);
    public RefactorChangeAt At(Anchor anchor) => At(anchor.ToWire());
    public RefactorChangeAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class RefactorChangeAt(IScriptToolBus bus, PlanContext plan, string anchorTarget)
{
    public RefactorChangeApply To(TypeIntent type) => new(bus, plan, anchorTarget, type);

    public RefactorChangeApply To(Anchor typeAnchor)
    {
        var span = typeAnchor.ToSpan();
        var id = span.MemberKey;
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("To(Anchor) needs M:TypeName on the created type");
        return To(Types.Of(id!));
    }

    public RefactorChangeApply To(string typeIdentifier) => To(Types.Of(typeIdentifier));
}

public sealed class RefactorChangeApply(IScriptToolBus bus, PlanContext plan, string anchorTarget, TypeIntent type)
{
    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        RefactorChangeRunner.RunAsync(bus, plan, anchorTarget, type, ct);
}

internal static class RefactorChangeRunner
{
    public const string Kind = "refactor.change";

    public static Task<StepResponse> RunAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string anchorTarget,
        TypeIntent type,
        CancellationToken ct)
    {
        _ = ct;
        var lang = (plan.Language ?? "csharp").Trim().ToLowerInvariant();
        if (lang is not "csharp")
            return Task.FromResult(StepResponse.Fail(Kind, "csharp-only v1"));
        if (!TypeProjection.TryProject(lang, type, out var typeWire, out var terr))
            return Task.FromResult(StepResponse.Fail(Kind, terr ?? "type project failed"));

        if (!AnchorLocus.TryResolveFile(plan, anchorTarget, Kind, out var file, out var span, out var fail))
            return Task.FromResult(fail!);
        if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(StepResponse.Fail(Kind, "csharp only (.cs)"));

        if (!BracketSyntaxResolve.TryFindAttachTarget(file, span, out var target, out var detail))
            return Task.FromResult(StepResponse.Fail(Kind, "locate failed: " + detail, new { anchor = anchorTarget }));

        if (target.Node is not TypeSyntax typeNode)
            return Task.FromResult(StepResponse.Fail(Kind, "Change.At requires K:ReturnType or K:Type (TypeSyntax)", new
            {
                node = target.Node.Kind().ToString(),
                locate = detail
            }));

        var text = File.ReadAllText(file);
        var s = typeNode.Span;
        var newText = text[..s.Start] + typeWire + text[s.End..];
        var args = ScriptArgs.From(new { path = file, anchor = anchorTarget, type = typeWire });

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(Kind, "dry_run", new { dry_run = true, path = file, type = typeWire });
            bus.RecordLocal("refactor", Kind, args, dry.ToJson(), skippedDryRun: true);
            return Task.FromResult(dry);
        }

        File.WriteAllText(file, newText);
        var ok = StepResponse.Success(Kind, "changed type → " + typeWire, new
        {
            path = file,
            anchor = anchorTarget,
            type = typeWire
        });
        bus.RecordLocal("refactor", Kind, args, ok.ToJson());
        return Task.FromResult(ok);
    }
}

internal static class MemberAnchorNames
{
    /// <summary>Resolve Members(Anchor…) → identifier list via M:.</summary>
    public static bool TryResolve(
        string kind,
        Anchor[] anchors,
        out string[] names,
        out StepResponse? fail)
    {
        names = new string[anchors.Length];
        for (var i = 0; i < anchors.Length; i++)
        {
            var key = anchors[i].ToSpan().MemberKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                fail = StepResponse.Fail(kind, "Members(Anchor) requires M:memberName on each anchor");
                names = [];
                return false;
            }

            names[i] = key!;
        }

        fail = null;
        return true;
    }
}
