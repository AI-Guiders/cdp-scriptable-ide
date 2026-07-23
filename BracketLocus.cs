using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cdp.ScriptableIde;

/// <summary>Shared bracket → file / type position for Generate &amp; Refactor.Move.</summary>
internal static class BracketLocus
{
    public static bool TryResolveFile(
        PlanContext plan,
        string bracketTarget,
        string kind,
        out string file,
        out BracketLocate.Span span,
        out StepResponse? fail)
    {
        file = "";
        span = BracketLocate.Parse(bracketTarget);
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

    public static bool TryResolveTypePosition(
        PlanContext plan,
        string bracketTarget,
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
        if (!TryResolveFile(plan, bracketTarget, kind, out file, out var span, out fail))
            return false;

        if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            fail = StepResponse.Fail(kind, "csharp only (.cs)", new { file });
            return false;
        }

        if (!BracketSyntaxResolve.TryFindAttachTarget(file, span, out var target, out var detail))
        {
            // Type-only: M:TypeName without body node — search type by name
            if (!string.IsNullOrWhiteSpace(span.MemberKey)
                && TryFindTypeDeclaration(file, span.MemberKey!, out var typeDecl, out var typeDetail))
            {
                var idSpan = typeDecl.Identifier.GetLocation().GetLineSpan();
                line = idSpan.StartLinePosition.Line + 1;
                column = idSpan.StartLinePosition.Character + 1;
                typeName = typeDecl.Identifier.Text;
                fail = null;
                return true;
            }

            fail = StepResponse.Fail(kind, $"locate failed: {detail}", new { bracket = bracketTarget });
            return false;
        }

        var type = FindType(target.Node);
        if (type is null)
        {
            fail = StepResponse.Fail(kind, "bracket must resolve to a type (use M:TypeName)", new
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
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(file));
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
}
