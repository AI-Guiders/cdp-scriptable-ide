using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cdp.ScriptableIde;

/// <summary>Shared Anchor / bracket-wire → file / type position for Generate &amp; Refactor.Move.</summary>
internal static class AnchorLocus
{
    public static bool TryResolveFile(
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
            fail = StepResponse.Fail(kind, "anchor must include F:path (bracket wire)");
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

    public static bool TryResolveTypePosition(
        PlanContext plan,
        string anchorTarget,
        string kind,
        out string file,
        out int line,
        out int column,
        out string typeName,
        out StepResponse? fail)
    {
        line = 0;
        column = 0;
        typeName = "";
        if (!TryResolveFile(plan, anchorTarget, kind, out file, out var span, out fail))
            return false;

        if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            fail = StepResponse.Fail(kind, "csharp only (.cs)", new { file });
            return false;
        }

        if (!BracketSyntaxResolve.TryFindAttachTarget(file, span, out var target, out var detail))
        {
            if (!string.IsNullOrWhiteSpace(span.MemberKey)
                && TryFindTypeDeclaration(file, span.MemberKey!, out var typeDecl, out var typeDetail))
            {
                _ = typeDetail;
                var idSpan = typeDecl.Identifier.GetLocation().GetLineSpan();
                line = idSpan.StartLinePosition.Line + 1;
                column = idSpan.StartLinePosition.Character + 1;
                typeName = typeDecl.Identifier.Text;
                fail = null;
                return true;
            }

            fail = StepResponse.Fail(kind, $"locate failed: {detail}", new { anchor = anchorTarget });
            return false;
        }

        var type = FindType(target.Node);
        if (type is null)
        {
            fail = StepResponse.Fail(kind, "anchor must resolve to a type (use M:TypeName)", new
            {
                node = target.Node.Kind().ToString(),
                locate = detail
            });
            return false;
        }

        var loc = type.Identifier.GetLocation().GetLineSpan();
        line = loc.StartLinePosition.Line + 1;
        column = loc.StartLinePosition.Character + 1;
        typeName = type.Identifier.Text;
        fail = null;
        return true;
    }

    public static string RequireSolution(PlanContext plan, string kind, out StepResponse? fail)
    {
        fail = null;
        if (!string.IsNullOrWhiteSpace(plan.SolutionOrProjectPath))
            return plan.SolutionOrProjectPath!;
        fail = StepResponse.Fail(kind, "solution_or_project_path required (cdp_open .sln/.csproj).");
        return "";
    }

    private static TypeDeclarationSyntax? FindType(SyntaxNode node)
    {
        for (var n = node; n is not null; n = n.Parent)
        {
            if (n is TypeDeclarationSyntax t)
                return t;
        }

        return null;
    }

    private static bool TryFindTypeDeclaration(
        string file,
        string typeName,
        out TypeDeclarationSyntax type,
        out string detail)
    {
        type = null!;
        detail = "";
        try
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            var hit = tree.GetCompilationUnitRoot()
                .DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(t => t.Identifier.Text.Equals(typeName, StringComparison.Ordinal));
            if (hit is null)
            {
                detail = "type_not_found";
                return false;
            }

            type = hit;
            detail = "type_name";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    public static bool TryResolveTextRange(
        PlanContext plan,
        string anchorTarget,
        string kind,
        out string file,
        out BracketSyntaxResolve.TextRange range,
        out StepResponse? fail)
    {
        range = default!;
        if (!TryResolveFile(plan, anchorTarget, kind, out file, out var span, out fail))
            return false;

        if (span.LineStart is int ls)
        {
            var le = span.LineEnd ?? ls;
            range = new BracketSyntaxResolve.TextRange(ls, 1, le, 1);
            fail = null;
            return true;
        }

        // Fall back: whole first line if no L: in wire.
        range = new BracketSyntaxResolve.TextRange(1, 1, 1, 1);
        fail = null;
        return true;
    }

    public static bool TryResolveCaret(
        PlanContext plan,
        string anchorTarget,
        string kind,
        out string file,
        out int line,
        out int column,
        out StepResponse? fail)
    {
        line = 1;
        column = 1;
        if (!TryResolveTextRange(plan, anchorTarget, kind, out file, out var range, out fail))
            return false;
        line = range.LineStart;
        column = range.ColumnStart;
        fail = null;
        return true;
    }

    public static bool TryMergeZones(
        BracketSyntaxResolve.TextRange from,
        BracketSyntaxResolve.TextRange till,
        string kind,
        out BracketSyntaxResolve.TextRange merged,
        out StepResponse? fail)
    {
        fail = null;
        var lineStart = Math.Min(from.LineStart, till.LineStart);
        var lineEnd = Math.Max(from.LineEnd, till.LineEnd);
        var colStart = from.LineStart <= till.LineStart ? from.ColumnStart : till.ColumnStart;
        var colEnd = from.LineEnd >= till.LineEnd ? from.ColumnEnd : till.ColumnEnd;
        merged = new BracketSyntaxResolve.TextRange(lineStart, colStart, lineEnd, colEnd);
        return true;
    }
}
