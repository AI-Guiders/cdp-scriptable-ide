using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cdp.ScriptableIde;

/// <summary>Annotate intents — Comment / DocComment at 0128 bracket (MLP §3.9).</summary>
public sealed class AnnotateFacade(ScriptToolBus bus, PlanContext plan)
{
    public AnnotateCommentFactory Comment => new(bus, plan);
    public AnnotateDocCommentFactory DocComment => new(bus, plan);
}

public sealed class AnnotateCommentFactory(ScriptToolBus bus, PlanContext plan)
{
    /// <summary>Escape: raw 0128 wire string.</summary>
    public AnnotateCommentAt At(string anchorTarget) => new(bus, plan, anchorTarget);

    public AnnotateCommentAt At(Anchor anchor) => At(anchor.ToWire());

    public AnnotateCommentAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class AnnotateDocCommentFactory(ScriptToolBus bus, PlanContext plan)
{
    /// <summary>Escape: raw 0128 wire string.</summary>
    public AnnotateDocCommentAt At(string anchorTarget) => new(bus, plan, anchorTarget);

    public AnnotateDocCommentAt At(Anchor anchor) => At(anchor.ToWire());

    public AnnotateDocCommentAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));
}

public sealed class AnnotateCommentAt(ScriptToolBus bus, PlanContext plan, string anchorTarget)
{
    private string? _text;

    public AnnotateCommentAt WithText(string text)
    {
        _text = text;
        return this;
    }

    /// <summary>Alias for <see cref="WithText"/>.</summary>
    public AnnotateCommentAt Content(string text) => WithText(text);

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        AnnotateRunner.ApplyCommentAsync(bus, plan, anchorTarget, _text, ct);
}

public sealed class AnnotateDocCommentAt(ScriptToolBus bus, PlanContext plan, string anchorTarget)
{
    private readonly DocModel _model = new();

    public AnnotateDocCommentAt Summary(string text)
    {
        _model.Summary = text;
        return this;
    }

    public AnnotateDocCommentAt Param(string name, string text)
    {
        _model.Params.Add(new DocParam(name, text));
        return this;
    }

    public AnnotateDocCommentAt Returns(string text)
    {
        _model.Returns = text;
        return this;
    }

    public AnnotateDocCommentAt Throws(string type, string text)
    {
        _model.Throws.Add(new DocThrows(type, text));
        return this;
    }

    public AnnotateDocCommentAt Throws(string text)
    {
        _model.Throws.Add(new DocThrows(null, text));
        return this;
    }

    public AnnotateDocCommentAt Remarks(string text)
    {
        _model.Remarks = text;
        return this;
    }

    public AnnotateDocCommentAt SeeAlso(string cref)
    {
        _model.SeeAlso.Add(cref);
        return this;
    }

    public AnnotateDocCommentAt Examples(string text)
    {
        _model.Examples = text;
        return this;
    }

    public AnnotateDocCommentAt WithText(string raw)
    {
        _model.Raw = raw;
        return this;
    }

    public AnnotateDocCommentAt Content(string raw) => WithText(raw);

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        AnnotateRunner.ApplyDocCommentAsync(bus, plan, anchorTarget, _model, ct);
}

internal static class AnnotateRunner
{
    public const string CommentKind = "annotate.comment";
    public const string DocKind = "annotate.doc_comment";

    public static Task<StepResponse> ApplyCommentAsync(
        ScriptToolBus bus,
        PlanContext plan,
        string anchorTarget,
        string? text,
        CancellationToken ct)
    {
        _ = ct;
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(StepResponse.Fail(CommentKind, "WithText/Content is required"));

        if (!TryResolveFile(plan, anchorTarget, CommentKind, out var file, out var span, out var fail))
            return Task.FromResult(fail!);

        if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(StepResponse.Fail(CommentKind, "csharp projection only (.cs)", new { file }));

        if (!BracketSyntaxResolve.TryFindAttachTarget(file, span, out var target, out var detail))
            return Task.FromResult(StepResponse.Fail(CommentKind, $"locate failed: {detail}", new { bracket = anchorTarget }));

        var indent = GetLineIndent(target.Tree, target.Node);
        var leading = BuildLineCommentTrivia(text!, indent);
        var kept = StripTrailingWhitespaceAndEol(KeepNonDocLeading(target.Node.GetLeadingTrivia()));
        var newLeading = SyntaxFactory.TriviaList(
            kept.Concat(leading).Append(SyntaxFactory.Whitespace(indent)));
        var newNode = target.Node.WithLeadingTrivia(newLeading);
        var newRoot = target.Root.ReplaceNode(target.Node, newNode);
        return WriteAsync(bus, plan, CommentKind, file, target, newRoot, text!, warnings: [], detail);
    }

    public static Task<StepResponse> ApplyDocCommentAsync(
        ScriptToolBus bus,
        PlanContext plan,
        string anchorTarget,
        DocModel model,
        CancellationToken ct)
    {
        _ = ct;
        if (model.IsEmpty)
            return Task.FromResult(StepResponse.Fail(DocKind, "DocModel empty — Summary/Param/… or WithText required"));

        if (!TryResolveFile(plan, anchorTarget, DocKind, out var file, out var span, out var fail))
            return Task.FromResult(fail!);

        if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(StepResponse.Fail(DocKind, "csharp projection only (.cs)", new { file }));

        if (!BracketSyntaxResolve.TryFindAttachTarget(file, span, out var target, out var detail))
            return Task.FromResult(StepResponse.Fail(DocKind, $"locate failed: {detail}", new { bracket = anchorTarget }));

        var member = FindDeclarable(target.Node);
        if (member is null)
            return Task.FromResult(StepResponse.Fail(DocKind,
                "DocComment needs type/member declarator (use M: or L: on member)",
                new { node = target.Node.Kind().ToString(), locate = detail }));

        var xml = CsharpXmlDocProjection.Format(model, out var warnings);
        var indent = GetLineIndent(target.Tree, member);
        var indented = string.Join("\n",
            xml.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => indent + line)) + "\n";
        var docTrivia = SyntaxFactory.ParseLeadingTrivia(indented);
        var kept = StripTrailingWhitespaceAndEol(KeepNonDocLeading(member.GetLeadingTrivia()));
        var newLeading = SyntaxFactory.TriviaList(
            kept.Concat(docTrivia).Append(SyntaxFactory.Whitespace(indent)));
        var newMember = member.WithLeadingTrivia(newLeading);
        var newRoot = target.Root.ReplaceNode(member, newMember);
        return WriteAsync(bus, plan, DocKind, file, target with { Node = member, Detail = detail }, newRoot,
            preview: xml.Trim(), warnings, detail);
    }

    private static Task<StepResponse> WriteAsync(
        ScriptToolBus bus,
        PlanContext plan,
        string kind,
        string file,
        BracketSyntaxResolve.AttachTarget target,
        CompilationUnitSyntax newRoot,
        string preview,
        string[] warnings,
        string locateDetail)
    {
        var newText = newRoot.ToFullString();
        var args = ScriptArgs.From(new
        {
            file,
            bracket_locate = locateDetail,
            node = target.Node.Kind().ToString(),
            warnings
        });

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(kind, "dry_run", new
            {
                dry_run = true,
                path = file,
                locate = locateDetail,
                would_change = true,
                preview,
                warnings
            });
            bus.RecordLocal("annotate", kind, args, dry.ToJson(), skippedDryRun: true);
            return Task.FromResult(dry);
        }

        File.WriteAllText(file, newText);
        var result = StepResponse.Success(kind, "applied", new
        {
            path = file,
            locate = locateDetail,
            node = target.Node.Kind().ToString(),
            preview,
            warnings,
            work_root = plan.WorkRoot
        });
        bus.RecordLocal("annotate", kind, args, result.ToJson(), skippedDryRun: false);
        return Task.FromResult(result);
    }

    private static bool TryResolveFile(
        PlanContext plan,
        string anchorTarget,
        string kind,
        out string file,
        out BracketLocate.Span span,
        out StepResponse? fail)
    {
        file = "";
        span = BracketLocate.Parse(anchorTarget);
        fail = null;
        if (string.IsNullOrWhiteSpace(span.File))
        {
            fail = StepResponse.Fail(kind, "bracket must include F:path");
            return false;
        }

        file = span.File!;
        if (!Path.IsPathRooted(file))
        {
            var root = plan.WorkRoot;
            if (string.IsNullOrWhiteSpace(root))
            {
                fail = StepResponse.Fail(kind, "relative F: needs Plan.WorkRoot (cdp_open) or absolute F:");
                return false;
            }
            file = Path.GetFullPath(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
        }
        else
        {
            file = Path.GetFullPath(file);
        }

        file = plan.Resolve(file);
        return true;
    }

    private static MemberDeclarationSyntax? FindDeclarable(SyntaxNode node)
    {
        for (var n = node; n is not null; n = n.Parent)
        {
            if (n is MemberDeclarationSyntax member)
                return member;
        }
        return null;
    }

    private static SyntaxTriviaList KeepNonDocLeading(SyntaxTriviaList leading) =>
        SyntaxFactory.TriviaList(leading.Where(t =>
            !t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
            && !t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)));

    /// <summary>Drop trailing whitespace/EOL so we can re-attach indent after inserted comments.</summary>
    private static SyntaxTriviaList StripTrailingWhitespaceAndEol(SyntaxTriviaList leading)
    {
        var list = leading.ToList();
        while (list.Count > 0)
        {
            var t = list[^1];
            if (t.IsKind(SyntaxKind.WhitespaceTrivia)
                || t.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                list.RemoveAt(list.Count - 1);
                continue;
            }
            break;
        }
        // Ensure a newline before our inserted block when prior trivia remains (e.g. attributes).
        if (list.Count > 0 && !list[^1].IsKind(SyntaxKind.EndOfLineTrivia))
            list.Add(SyntaxFactory.EndOfLine("\n"));
        else if (list.Count == 0)
            list.Add(SyntaxFactory.EndOfLine("\n"));
        return SyntaxFactory.TriviaList(list);
    }

    private static string GetLineIndent(SyntaxTree tree, SyntaxNode node)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        var line = tree.GetText().Lines[lineSpan.StartLinePosition.Line];
        var lineText = tree.GetText().ToString(line.Span);
        var trim = lineText.Length - lineText.TrimStart().Length;
        return lineText[..trim];
    }

    private static SyntaxTriviaList BuildLineCommentTrivia(string text, string indent)
    {
        var list = new List<SyntaxTrivia>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var line in lines)
        {
            list.Add(SyntaxFactory.Whitespace(indent));
            list.Add(SyntaxFactory.Comment("// " + line));
            list.Add(SyntaxFactory.EndOfLine("\n"));
        }
        return SyntaxFactory.TriviaList(list);
    }
}
