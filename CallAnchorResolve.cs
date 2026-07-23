using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cdp.ScriptableIde;

/// <summary>Resolve method Anchor → containing type name + method name for Act.Call.</summary>
internal static class CallAnchorResolve
{
    public static bool TryResolve(
        PlanContext plan,
        string methodAnchor,
        out string typeName,
        out string methodName,
        out string? error)
    {
        typeName = "";
        methodName = "";
        error = null;
        if (!AnchorLocus.TryResolveFile(plan, methodAnchor, "act.call", out var file, out var span, out var fail))
        {
            error = fail?.Error ?? "anchor file resolve failed";
            return false;
        }

        if (string.IsNullOrWhiteSpace(span.MemberKey))
        {
            error = "Call(Anchor) needs M:MethodName";
            return false;
        }

        methodName = span.MemberKey!;
        var wantMethod = methodName;
        if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            // non-csharp: treat MemberKey as method; type ≈ file basename (thin)
            typeName = Path.GetFileNameWithoutExtension(file);
            return true;
        }

        try
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            var root = tree.GetCompilationUnitRoot();
            var method = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text.Equals(wantMethod, StringComparison.Ordinal));
            if (method is null)
            {
                error = "method not found: " + wantMethod;
                return false;
            }

            var type = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (type is null)
            {
                error = "containing type not found for " + wantMethod;
                return false;
            }

            typeName = type.Identifier.Text;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
