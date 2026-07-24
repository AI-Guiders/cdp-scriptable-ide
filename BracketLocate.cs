using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cdp.ScriptableIde;

/// <summary>0128 L2 bracket locate for mutate (F/M/L/S/K csharp + X/A xml).</summary>
public static partial class BracketLocate
{
    public enum AxisFamily
    {
        None,
        Csharp,
        Xml
    }

    static readonly HashSet<string> KnownAxes = new(StringComparer.OrdinalIgnoreCase)
    {
        "F", "M", "L", "S", "K", "X", "A"
    };

    [GeneratedRegex(@"\bF:(?<file>[^;\]]+)", RegexOptions.CultureInvariant)]
    private static partial Regex FileToken();

    [GeneratedRegex(@"\bM:(?<member>[^;\]]+)", RegexOptions.CultureInvariant)]
    private static partial Regex MemberToken();

    [GeneratedRegex(@"\bL:(?<start>\d+)\s*(?:-\s*(?<end>\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex LineToken();

    /// <summary>S:kind:index — e.g. S:if:1 (1-based among siblings of that kind).</summary>
    [GeneratedRegex(@"\bS:(?<kind>[A-Za-z]+)(?::(?<index>\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex ScopeToken();

    /// <summary>K: csharp roles | xml Element|Attr.</summary>
    [GeneratedRegex(@"\bK:(?<role>[^;\]]+)", RegexOptions.CultureInvariant)]
    private static partial Regex RoleToken();

    /// <summary>X:Project/PropertyGroup/OutputType or ItemGroup/PackageReference@Include=Foo.</summary>
    [GeneratedRegex(@"\bX:(?<path>[^;\]]+)", RegexOptions.CultureInvariant)]
    private static partial Regex XmlPathToken();

    /// <summary>A:Version — attribute value on the X: element.</summary>
    [GeneratedRegex(@"\bA:(?<attr>[^;\]]+)", RegexOptions.CultureInvariant)]
    private static partial Regex AttrToken();

    [GeneratedRegex(@"\b([A-Za-z]+):", RegexOptions.CultureInvariant)]
    private static partial Regex AnyAxisToken();

    public sealed record Span(
        string? File,
        string? MemberKey,
        int? LineStart,
        int? LineEnd,
        string? ScopeKind = null,
        int? ScopeIndex = null,
        string? Role = null,
        string? XmlPath = null,
        string? Attr = null);

    public static Span Parse(string bracketOrInner)
    {
        var text = (bracketOrInner ?? "").Trim();
        if (text.StartsWith('[') && text.EndsWith(']'))
            text = text[1..^1].Trim();

        foreach (Match am in AnyAxisToken().Matches(text))
        {
            var key = am.Groups[1].Value;
            if (!KnownAxes.Contains(key))
                throw new ArgumentException($"unknown_axis:{key}");
        }

        string? file = null;
        string? member = null;
        int? lineStart = null;
        int? lineEnd = null;
        string? scopeKind = null;
        int? scopeIndex = null;
        string? role = null;
        string? xmlPath = null;
        string? attr = null;

        var fm = FileToken().Match(text);
        if (fm.Success)
            file = fm.Groups["file"].Value.Trim();

        var mm = MemberToken().Match(text);
        if (mm.Success)
            member = mm.Groups["member"].Value.Trim();

        var lm = LineToken().Match(text);
        if (lm.Success)
        {
            lineStart = int.Parse(lm.Groups["start"].Value);
            lineEnd = lm.Groups["end"].Success
                ? int.Parse(lm.Groups["end"].Value)
                : lineStart;
        }

        var sm = ScopeToken().Match(text);
        if (sm.Success)
        {
            scopeKind = sm.Groups["kind"].Value.Trim().ToLowerInvariant();
            scopeIndex = sm.Groups["index"].Success
                ? int.Parse(sm.Groups["index"].Value)
                : 1;
        }

        var km = RoleToken().Match(text);
        if (km.Success)
            role = km.Groups["role"].Value.Trim();

        var xm = XmlPathToken().Match(text);
        if (xm.Success)
            xmlPath = xm.Groups["path"].Value.Trim();

        var am2 = AttrToken().Match(text);
        if (am2.Success)
            attr = am2.Groups["attr"].Value.Trim();

        var span = new Span(file, member, lineStart, lineEnd, scopeKind, scopeIndex, role, xmlPath, attr);
        _ = ClassifyFamily(span, out var familyError);
        if (familyError is not null)
            throw new ArgumentException(familyError);
        return span;
    }

    /// <summary>
    /// Discriminate csharp (M/S/L) vs xml (X/A). Shared: F, K. Mixed → error.
    /// </summary>
    public static AxisFamily ClassifyFamily(Span span, out string? error)
    {
        error = null;
        var hasCsharpStructural = !string.IsNullOrWhiteSpace(span.MemberKey)
            || !string.IsNullOrWhiteSpace(span.ScopeKind)
            || span.LineStart is not null;
        var hasXml = !string.IsNullOrWhiteSpace(span.XmlPath)
            || !string.IsNullOrWhiteSpace(span.Attr);

        if (hasCsharpStructural && hasXml)
        {
            error = "mixed_axes";
            return AxisFamily.None;
        }

        if (hasXml)
        {
            if (string.IsNullOrWhiteSpace(span.XmlPath) && !string.IsNullOrWhiteSpace(span.Attr))
            {
                error = "need_X_for_A";
                return AxisFamily.None;
            }

            return AxisFamily.Xml;
        }

        if (hasCsharpStructural || !string.IsNullOrWhiteSpace(span.Role))
            return AxisFamily.Csharp;

        return AxisFamily.None;
    }

    /// <summary>Emit 0128 wire from a structured span (agent surface prefers <see cref="Anchor"/>).</summary>
    public static string Format(Span span)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(span.File))
            parts.Add("F:" + span.File.Trim());
        if (!string.IsNullOrWhiteSpace(span.MemberKey))
            parts.Add("M:" + span.MemberKey.Trim());
        if (span.LineStart is int ls)
        {
            parts.Add(span.LineEnd is int le && le != ls
                ? $"L:{ls}-{le}"
                : $"L:{ls}");
        }

        if (!string.IsNullOrWhiteSpace(span.ScopeKind))
        {
            var kind = span.ScopeKind.Trim().ToLowerInvariant();
            var idx = span.ScopeIndex is > 0 ? span.ScopeIndex.Value : 1;
            parts.Add(idx == 1 ? $"S:{kind}" : $"S:{kind}:{idx}");
        }

        if (!string.IsNullOrWhiteSpace(span.XmlPath))
            parts.Add("X:" + span.XmlPath.Trim());
        if (!string.IsNullOrWhiteSpace(span.Attr))
            parts.Add("A:" + span.Attr.Trim());
        if (!string.IsNullOrWhiteSpace(span.Role))
            parts.Add("K:" + span.Role.Trim());

        return "[" + string.Join(';', parts) + "]";
    }
}

/// <summary>Resolve S(+K) to line/column range via local C# parse (no MSBuild).</summary>
public static class BracketSyntaxResolve
{
    public sealed record TextRange(int LineStart, int ColumnStart, int LineEnd, int ColumnEnd);

    public sealed record AttachTarget(
        string AbsolutePath,
        SyntaxTree Tree,
        CompilationUnitSyntax Root,
        SyntaxNode Node,
        string Detail);

    public static bool TryResolve(string absoluteFilePath, BracketLocate.Span span, out TextRange range, out string detail) =>
        TryResolve(absoluteFilePath, sourceText: null, span, out range, out detail);

    /// <param name="sourceText">
    /// Optional buffer text (cdp_buffer). When null, reads <paramref name="absoluteFilePath"/> from disk.
    /// </param>
    public static bool TryResolve(
        string absoluteFilePath,
        string? sourceText,
        BracketLocate.Span span,
        out TextRange range,
        out string detail)
    {
        if (!TryFindAttachTarget(absoluteFilePath, sourceText, span, out var target, out detail))
        {
            range = default!;
            return false;
        }

        var lineSpan = target.Node.GetLocation().GetLineSpan();
        range = new TextRange(
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1,
            lineSpan.EndLinePosition.Line + 1,
            Math.Max(1, lineSpan.EndLinePosition.Character + 1));
        detail = target.Detail;
        return true;
    }

    /// <summary>Resolve F+(M|L|S[+K]) to a syntax node for annotate/mutate attach.</summary>
    public static bool TryFindAttachTarget(
        string absoluteFilePath,
        BracketLocate.Span span,
        out AttachTarget target,
        out string detail) =>
        TryFindAttachTarget(absoluteFilePath, sourceText: null, span, out target, out detail);

    /// <param name="sourceText">When set, parse this instead of disk (dirty buffer / in-memory).</param>
    public static bool TryFindAttachTarget(
        string absoluteFilePath,
        string? sourceText,
        BracketLocate.Span span,
        out AttachTarget target,
        out string detail)
    {
        target = default!;
        detail = "";
        string text;
        if (sourceText is not null)
        {
            text = sourceText;
        }
        else if (File.Exists(absoluteFilePath))
        {
            text = File.ReadAllText(absoluteFilePath);
        }
        else
        {
            detail = "file_missing";
            return false;
        }

        var tree = CSharpSyntaxTree.ParseText(text);
        var root = tree.GetCompilationUnitRoot();

        SyntaxNode searchRoot = root;
        MemberDeclarationSyntax? member = null;
        if (!string.IsNullOrWhiteSpace(span.MemberKey))
        {
            member = root.DescendantNodes()
                .OfType<MemberDeclarationSyntax>()
                .FirstOrDefault(m => MemberName(m).Equals(span.MemberKey, StringComparison.Ordinal));
            if (member is null)
            {
                detail = "member_not_found";
                return false;
            }
            searchRoot = member;
        }

        SyntaxNode? focus;
        string resolveDetail;

        if (!string.IsNullOrWhiteSpace(span.ScopeKind))
        {
            if (!TryResolveScope(searchRoot, span, out focus, out resolveDetail))
                return Fail(resolveDetail, out detail);
        }
        else if (span.LineStart is >= 1)
        {
            focus = FindNodeAtLine(tree, root, searchRoot, span.LineStart.Value);
            if (focus is null)
                return Fail("line_node_not_found", out detail);
            if (!string.IsNullOrWhiteSpace(span.Role))
            {
                if (!TryApplyLineRole(focus, span.Role.Trim(), out focus, out resolveDetail))
                    return Fail(resolveDetail, out detail);
            }
            else
            {
                resolveDetail = "line";
            }
        }
        else if (member is not null)
        {
            focus = member;
            resolveDetail = "member";
            if (!string.IsNullOrWhiteSpace(span.Role))
            {
                if (!TryApplyMemberRole(member, span.Role.Trim(), out focus, out resolveDetail))
                    return Fail(resolveDetail, out detail);
            }
        }
        else
        {
            return Fail("need_M_or_L_or_S", out detail);
        }

        if (focus is null)
            return Fail("node_null", out detail);

        target = new AttachTarget(absoluteFilePath, tree, root, focus, resolveDetail);
        detail = resolveDetail;
        return true;
    }

    /// <summary>
    /// Roles on L: (no S:) — Initializer / Type / Name / Parameter of local or nearby decl.
    /// </summary>
    private static bool TryApplyLineRole(
        SyntaxNode node,
        string role,
        out SyntaxNode? focus,
        out string detail)
    {
        focus = node;
        detail = "";

        var isInit = role.Equals("Initializer", StringComparison.OrdinalIgnoreCase)
                     || role.Equals("Value", StringComparison.OrdinalIgnoreCase)
                     || role.Equals("Rhs", StringComparison.OrdinalIgnoreCase);
        if (!isInit)
        {
            for (var n = node; n is not null; n = n.Parent)
            {
                if (n is MemberDeclarationSyntax or LocalDeclarationStatementSyntax or ParameterSyntax
                    or LocalFunctionStatementSyntax or VariableDeclaratorSyntax)
                {
                    if (TryApplyMemberRole(n, role, out focus, out detail))
                        return true;
                }
            }

            detail = $"unknown_line_role:{role}";
            return false;
        }

        var local = node.AncestorsAndSelf().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
        if (local is not null)
        {
            var value = local.Declaration.Variables
                .Select(v => v.Initializer?.Value)
                .FirstOrDefault(v => v is not null);
            if (value is null)
            {
                detail = "no_initializer";
                return false;
            }

            focus = value;
            detail = "line+Initializer";
            return true;
        }

        var field = node.AncestorsAndSelf().OfType<FieldDeclarationSyntax>().FirstOrDefault();
        if (field is not null)
        {
            var value = field.Declaration.Variables
                .Select(v => v.Initializer?.Value)
                .FirstOrDefault(v => v is not null);
            if (value is null)
            {
                detail = "no_initializer";
                return false;
            }

            focus = value;
            detail = "line+Initializer";
            return true;
        }

        var assign = node.AncestorsAndSelf().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
        if (assign is not null)
        {
            focus = assign.Right;
            detail = "line+Rhs";
            return true;
        }

        if (node is EqualsValueClauseSyntax ev)
        {
            focus = ev.Value;
            detail = "line+Initializer";
            return true;
        }

        if (node.Parent is EqualsValueClauseSyntax evParent)
        {
            focus = evParent.Value;
            detail = "line+Initializer";
            return true;
        }

        detail = "initializer_not_found";
        return false;
    }

    private static bool Fail(string why, out string detail)
    {
        detail = why;
        return false;
    }

    private static bool TryResolveScope(
        SyntaxNode searchRoot,
        BracketLocate.Span span,
        out SyntaxNode? focus,
        out string detail)
    {
        focus = null;
        detail = "";
        var index = span.ScopeIndex is > 0 ? span.ScopeIndex.Value : 1;
        SyntaxNode? target = span.ScopeKind switch
        {
            "if" => searchRoot.DescendantNodes().OfType<IfStatementSyntax>().Skip(index - 1).FirstOrDefault(),
            "for" => searchRoot.DescendantNodes().OfType<ForStatementSyntax>().Skip(index - 1).FirstOrDefault(),
            "foreach" => searchRoot.DescendantNodes().OfType<ForEachStatementSyntax>().Skip(index - 1).FirstOrDefault(),
            "while" => searchRoot.DescendantNodes().OfType<WhileStatementSyntax>().Skip(index - 1).FirstOrDefault(),
            _ => null
        };

        if (target is null)
        {
            detail = $"scope_not_found:{span.ScopeKind}:{index}";
            return false;
        }

        focus = target;
        if (!string.IsNullOrWhiteSpace(span.Role))
        {
            var role = span.Role.Trim();
            if (!TryApplyRole(target, role, out focus, out detail))
                return false;
            detail = $"syntax_scope+{role}";
        }
        else
        {
            detail = "syntax_scope";
        }

        return true;
    }

    /// <summary>Roles on member / local / parameter nodes (K:Name, Parameter:x, ReturnType, Body, Type).</summary>
    private static bool TryApplyMemberRole(
        SyntaxNode target,
        string role,
        out SyntaxNode? focus,
        out string detail)
    {
        focus = target;
        detail = "";

        if (role.Equals("Name", StringComparison.OrdinalIgnoreCase))
        {
            focus = target switch
            {
                MethodDeclarationSyntax m => m,
                PropertyDeclarationSyntax p => p,
                TypeDeclarationSyntax t => t,
                ParameterSyntax p => p,
                VariableDeclaratorSyntax v => v,
                LocalDeclarationStatementSyntax loc => loc.Declaration.Variables.FirstOrDefault() ?? (SyntaxNode)loc,
                FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault() ?? (SyntaxNode)f,
                _ => target
            };
            detail = "member+Name";
            return true;
        }

        if (role.Equals("ReturnType", StringComparison.OrdinalIgnoreCase))
        {
            focus = target switch
            {
                MethodDeclarationSyntax m => m.ReturnType,
                PropertyDeclarationSyntax p => p.Type,
                _ => null
            };
            if (focus is null)
            {
                detail = "no_return_type";
                return false;
            }

            detail = "member+ReturnType";
            return true;
        }

        if (role.Equals("Body", StringComparison.OrdinalIgnoreCase))
        {
            focus = target switch
            {
                MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
                PropertyDeclarationSyntax p => p.ExpressionBody
                    ?? (SyntaxNode?)p.AccessorList,
                _ => null
            };
            if (focus is null)
            {
                detail = "no_body";
                return false;
            }

            detail = "member+Body";
            return true;
        }

        if (role.Equals("Type", StringComparison.OrdinalIgnoreCase))
        {
            focus = target switch
            {
                ParameterSyntax p => p.Type,
                PropertyDeclarationSyntax p => p.Type,
                VariableDeclaratorSyntax v when v.Parent is VariableDeclarationSyntax vd => vd.Type,
                LocalDeclarationStatementSyntax loc => loc.Declaration.Type,
                FieldDeclarationSyntax f => f.Declaration.Type,
                _ => null
            };
            if (focus is null)
            {
                detail = "no_type";
                return false;
            }

            detail = "member+Type";
            return true;
        }

        var isInit = role.Equals("Initializer", StringComparison.OrdinalIgnoreCase)
                     || role.Equals("Value", StringComparison.OrdinalIgnoreCase)
                     || role.Equals("Rhs", StringComparison.OrdinalIgnoreCase);
        if (isInit)
        {
            focus = target switch
            {
                PropertyDeclarationSyntax p => (SyntaxNode?)p.Initializer?.Value
                    ?? p.ExpressionBody?.Expression,
                FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Initializer?.Value,
                VariableDeclaratorSyntax v => v.Initializer?.Value,
                LocalDeclarationStatementSyntax loc =>
                    loc.Declaration.Variables.FirstOrDefault()?.Initializer?.Value,
                MethodDeclarationSyntax m => m.ExpressionBody?.Expression,
                _ => null
            };
            if (focus is null)
            {
                detail = "no_initializer";
                return false;
            }

            detail = "member+Initializer";
            return true;
        }

        if (role.StartsWith("Parameter:", StringComparison.OrdinalIgnoreCase))
        {
            var paramName = role["Parameter:".Length..].Trim();
            if (paramName.Length == 0)
            {
                detail = "parameter_name_empty";
                return false;
            }

            SyntaxNode? methodNode = target as MethodDeclarationSyntax
                ?? target as LocalFunctionStatementSyntax
                ?? (SyntaxNode?)target.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault()
                ?? target.AncestorsAndSelf().OfType<LocalFunctionStatementSyntax>().FirstOrDefault();

            SeparatedSyntaxList<ParameterSyntax>? parms = methodNode switch
            {
                MethodDeclarationSyntax m => m.ParameterList.Parameters,
                LocalFunctionStatementSyntax lf => lf.ParameterList.Parameters,
                _ => null
            };
            if (parms is null)
            {
                detail = "parameter_needs_method";
                return false;
            }

            var hit = parms.Value.FirstOrDefault(p => p.Identifier.Text.Equals(paramName, StringComparison.Ordinal));
            if (hit is null)
            {
                detail = $"parameter_not_found:{paramName}";
                return false;
            }

            focus = hit;
            detail = "member+Parameter";
            return true;
        }

        // Control-flow roles if target happens to be if/while/…
        if (TryApplyRole(target, role, out focus, out detail))
            return true;

        detail = $"unknown_role:{role}";
        return false;
    }

    private static bool TryApplyRole(
        SyntaxNode target,
        string role,
        out SyntaxNode? focus,
        out string detail)
    {
        focus = target;
        detail = "";

        var isCondition = role.Equals("Condition", StringComparison.OrdinalIgnoreCase);
        var isThen = role.Equals("Branch.True", StringComparison.OrdinalIgnoreCase)
                     || role.Equals("Then", StringComparison.OrdinalIgnoreCase);
        var isElse = role.Equals("Branch.False", StringComparison.OrdinalIgnoreCase)
                     || role.Equals("Else", StringComparison.OrdinalIgnoreCase);
        var isExpression = role.Equals("Expression", StringComparison.OrdinalIgnoreCase)
                           || role.Equals("Collection", StringComparison.OrdinalIgnoreCase);

        switch (target)
        {
            case IfStatementSyntax ifStmt:
                if (isCondition)
                {
                    focus = ifStmt.Condition;
                    return true;
                }

                if (isThen)
                {
                    focus = ifStmt.Statement;
                    return true;
                }

                if (isElse)
                {
                    if (ifStmt.Else is null)
                    {
                        detail = "no_else";
                        return false;
                    }

                    focus = ifStmt.Else.Statement;
                    return true;
                }

                detail = $"unknown_role:{role}";
                return false;

            case WhileStatementSyntax whileStmt:
                if (isCondition)
                {
                    focus = whileStmt.Condition;
                    return true;
                }

                if (isThen)
                {
                    focus = whileStmt.Statement;
                    return true;
                }

                detail = isElse ? "while_no_else" : $"unknown_role:{role}";
                return false;

            case ForStatementSyntax forStmt:
                if (isCondition)
                {
                    if (forStmt.Condition is null)
                    {
                        detail = "for_no_condition";
                        return false;
                    }

                    focus = forStmt.Condition;
                    return true;
                }

                if (isThen)
                {
                    focus = forStmt.Statement;
                    return true;
                }

                detail = isElse ? "for_no_else" : $"unknown_role:{role}";
                return false;

            case ForEachStatementSyntax foreachStmt:
                if (isCondition)
                {
                    detail = "foreach_no_condition_use_Expression";
                    return false;
                }

                if (isExpression)
                {
                    focus = foreachStmt.Expression;
                    return true;
                }

                if (isThen)
                {
                    focus = foreachStmt.Statement;
                    return true;
                }

                detail = isElse ? "foreach_no_else" : $"unknown_role:{role}";
                return false;

            default:
                detail = $"role_unsupported_scope:{target.Kind()}";
                return false;
        }
    }

    private static SyntaxNode? FindNodeAtLine(SyntaxTree tree, CompilationUnitSyntax root, SyntaxNode searchRoot, int line1Based)
    {
        var text = tree.GetText();
        if (line1Based < 1 || line1Based > text.Lines.Count)
            return null;
        var line = text.Lines[line1Based - 1];
        var span = line.Span;
        var lineText = text.ToString(line.Span);
        var trim = lineText.Length - lineText.TrimStart().Length;
        var pos = line.Start + Math.Min(trim, Math.Max(0, line.Span.Length - 1));
        var node = searchRoot.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, 0), findInsideTrivia: false, getInnermostNodeForTie: true);
        return node == root ? null : node;
    }

    private static string MemberName(MemberDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax method => method.Identifier.Text,
        ConstructorDeclarationSyntax ctor => ctor.Identifier.Text,
        PropertyDeclarationSyntax prop => prop.Identifier.Text,
        FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "",
        TypeDeclarationSyntax type => type.Identifier.Text,
        _ => ""
    };
}
