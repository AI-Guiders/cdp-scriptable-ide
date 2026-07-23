using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cdp.ScriptableIde;

/// <summary>Resolve <see cref="CodeAnchor"/> by symbol name (no manual line/column).</summary>
internal static class CodeAnchorResolve
{
    public static CodeAnchor Named(
        PlanContext plan,
        string filePath,
        string symbolName,
        string? solutionOrProjectPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolName);
        var file = ResolveReadableFile(plan, filePath.Trim());
        if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Symbol.Named csharp-only (.cs) v1.", nameof(filePath));
        if (!File.Exists(file))
            throw new FileNotFoundException("Symbol.Named: file not found.", file);

        var span = new BracketLocate.Span(file, symbolName.Trim(), null, null, null, null, null);
        if (!BracketSyntaxResolve.TryFindAttachTarget(file, span, out var target, out var detail))
        {
            // Types sometimes miss member walk — same fallback as AnchorLocus.
            if (!TryTypeIdentifier(file, symbolName.Trim(), out var line, out var column, out detail))
                throw new InvalidOperationException($"Symbol.Named('{symbolName}') locate failed: {detail}");
            return new CodeAnchor(file, line, column, solutionOrProjectPath ?? plan.SolutionOrProjectPath);
        }

        var id = TryIdentifier(target.Node);
        var loc = (id?.GetLocation() ?? target.Node.GetLocation()).GetLineSpan();
        return new CodeAnchor(
            file,
            loc.StartLinePosition.Line + 1,
            loc.StartLinePosition.Character + 1,
            solutionOrProjectPath ?? plan.SolutionOrProjectPath);
    }

    public static CodeAnchor FromAnchor(PlanContext plan, Anchor anchor, string? solutionOrProjectPath = null)
    {
        var wire = anchor.ToWire();
        if (!AnchorLocus.TryResolveFile(plan, wire, "code_anchor", out var file, out var span, out var fail))
            throw new InvalidOperationException(fail?.Error ?? "Anchor file resolve failed.");
        if (string.IsNullOrWhiteSpace(span.MemberKey))
            return CodeAnchor.File(file, solutionOrProjectPath ?? plan.SolutionOrProjectPath);
        return Named(plan, file, span.MemberKey!, solutionOrProjectPath);
    }

    /// <summary>Prefer existing absolute/work path; avoid worktree remap that drops a readable primary file.</summary>
    private static string ResolveReadableFile(PlanContext plan, string filePath)
    {
        var rooted = Path.IsPathRooted(filePath)
            ? Path.GetFullPath(filePath)
            : Path.GetFullPath(Path.Combine(plan.WorkRoot, filePath.Replace('/', Path.DirectorySeparatorChar)));
        if (File.Exists(rooted))
            return rooted;
        var mapped = plan.Resolve(filePath);
        if (File.Exists(mapped))
            return mapped;
        return rooted;
    }

    private static SyntaxToken? TryIdentifier(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier,
        TypeDeclarationSyntax t => t.Identifier,
        PropertyDeclarationSyntax p => p.Identifier,
        FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier,
        EventDeclarationSyntax e => e.Identifier,
        ConstructorDeclarationSyntax c => c.Identifier,
        DelegateDeclarationSyntax d => d.Identifier,
        EnumMemberDeclarationSyntax em => em.Identifier,
        LocalFunctionStatementSyntax lf => lf.Identifier,
        _ => null
    };

    private static bool TryTypeIdentifier(string file, string typeName, out int line, out int column, out string detail)
    {
        line = 0;
        column = 0;
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

            var loc = hit.Identifier.GetLocation().GetLineSpan();
            line = loc.StartLinePosition.Line + 1;
            column = loc.StartLinePosition.Character + 1;
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
