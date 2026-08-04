using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cdp.ScriptableIde;

/// <summary>
/// 0128 bracket locate — families <c>code</c> / <c>xml</c> / <c>navigation</c> (ADR 0186).
/// Full axis names canonical; short letters are aliases.
/// </summary>
public static class BracketLocate
{
    public enum AxisFamily
    {
        None,
        Csharp,
        Xml,
        Navigation
    }

    static readonly Dictionary<string, string> AxisAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["F"] = "File",
        ["File"] = "File",
        ["M"] = "Member",
        ["Member"] = "Member",
        ["L"] = "Line",
        ["Line"] = "Line",
        ["S"] = "Scope",
        ["Scope"] = "Scope",
        ["T"] = "Text",
        ["Text"] = "Text",
        ["Content"] = "Text",
        ["K"] = "Kind",
        ["Kind"] = "Kind",
        ["Role"] = "Kind",
        ["X"] = "Element",
        ["Element"] = "Element",
        ["A"] = "Attribute",
        ["Attribute"] = "Attribute",
        ["Attr"] = "Attribute",
        ["Family"] = "Family",
        ["Fam"] = "Family",
        ["Command"] = "Command",
        ["C"] = "Command",
        ["Go"] = "Go",
        ["G"] = "Go",
        ["Anchor"] = "Anchor",
        // legacy flag → Family:navigation
        ["N"] = "Navigate",
        ["Navigate"] = "Navigate",
    };

    public sealed record Span(
        string? File,
        string? MemberKey,
        int? LineStart,
        int? LineEnd,
        string? ScopeKind = null,
        int? ScopeIndex = null,
        string? Role = null,
        string? XmlPath = null,
        string? Attr = null,
        string? Family = null,
        string? Command = null,
        string? Go = null,
        Span? NestedAnchor = null,
        string? TextNeedle = null);

    public static Span Parse(string bracketOrInner)
    {
        var text = StripOuterBrackets(bracketOrInner);
        string? file = null;
        string? member = null;
        int? lineStart = null;
        int? lineEnd = null;
        string? scopeKind = null;
        int? scopeIndex = null;
        string? role = null;
        string? xmlPath = null;
        string? attr = null;
        string? family = null;
        string? command = null;
        string? go = null;
        string? textNeedle = null;
        Span? nested = null;
        var legacyNavigate = false;

        foreach (var (rawKey, rawVal) in SplitAxes(text))
        {
            if (!AxisAlias.TryGetValue(rawKey, out var canon))
                throw new ArgumentException($"unknown_axis:{rawKey}");

            var val = rawVal.Trim();
            switch (canon)
            {
                case "Family":
                    family = NormalizeFamilyName(val);
                    break;
                case "Navigate":
                    if (IsTruthy(val))
                        legacyNavigate = true;
                    break;
                case "File":
                    file = val;
                    break;
                case "Member":
                    member = val;
                    break;
                case "Line":
                    ParseLine(val, out lineStart, out lineEnd);
                    break;
                case "Text":
                    textNeedle = SanitizeTextNeedle(val);
                    break;
                case "Scope":
                    ParseScope(val, out scopeKind, out scopeIndex);
                    break;
                case "Kind":
                    role = val;
                    break;
                case "Element":
                    xmlPath = val;
                    break;
                case "Attribute":
                    attr = val;
                    break;
                case "Command":
                    command = val.ToLowerInvariant();
                    break;
                case "Go":
                    go = val;
                    break;
                case "Anchor":
                    nested = Parse(val);
                    break;
            }
        }

        if (legacyNavigate && string.IsNullOrWhiteSpace(family))
            family = "navigation";

        var span = new Span(
            file, member, lineStart, lineEnd, scopeKind, scopeIndex, role, xmlPath, attr,
            family, command, go, nested, textNeedle);
        _ = ClassifyFamily(span, out var familyError);
        if (familyError is not null)
            throw new ArgumentException(familyError);
        return span;
    }

    /// <summary>
    /// Explicit <c>Family:</c> wins; else infer code (M/S/L) vs xml (Element/Attribute).
    /// Navigation = Family or Command/Go/nested.
    /// </summary>
    public static AxisFamily ClassifyFamily(Span span, out string? error)
    {
        error = null;
        var fam = NormalizeFamilyName(span.Family);
        if (fam is "navigation" or "nav")
            return AxisFamily.Navigation;
        if (fam is "xml")
            return ValidateXml(span, out error) ? AxisFamily.Xml : AxisFamily.None;
        if (fam is "code" or "csharp" or "c#")
            return ValidateCode(span, out error) ? AxisFamily.Csharp : AxisFamily.None;

        var hasNav = !string.IsNullOrWhiteSpace(span.Command)
                     || !string.IsNullOrWhiteSpace(span.Go)
                     || span.NestedAnchor is not null;
        var hasCsharpStructural = !string.IsNullOrWhiteSpace(span.MemberKey)
            || !string.IsNullOrWhiteSpace(span.ScopeKind)
            || span.LineStart is not null
            || !string.IsNullOrWhiteSpace(span.TextNeedle);
        var hasXml = !string.IsNullOrWhiteSpace(span.XmlPath)
            || !string.IsNullOrWhiteSpace(span.Attr);

        if (hasNav && (hasCsharpStructural || hasXml))
        {
            // Nested Anchor may carry code/xml; outer nav axes alone are fine.
            if (!string.IsNullOrWhiteSpace(span.MemberKey)
                || !string.IsNullOrWhiteSpace(span.ScopeKind)
                || span.LineStart is not null
                || !string.IsNullOrWhiteSpace(span.TextNeedle)
                || !string.IsNullOrWhiteSpace(span.XmlPath)
                || !string.IsNullOrWhiteSpace(span.Attr))
            {
                error = "mixed_axes";
                return AxisFamily.None;
            }
        }

        if (hasNav)
            return AxisFamily.Navigation;

        if (hasCsharpStructural && hasXml)
        {
            error = "mixed_axes";
            return AxisFamily.None;
        }

        if (hasXml)
            return ValidateXml(span, out error) ? AxisFamily.Xml : AxisFamily.None;

        if (hasCsharpStructural || !string.IsNullOrWhiteSpace(span.Role))
            return AxisFamily.Csharp;

        // File-only → none (open path uses navigation or plain file label)
        return AxisFamily.None;
    }

    static bool ValidateXml(Span span, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(span.XmlPath) && !string.IsNullOrWhiteSpace(span.Attr))
        {
            error = "need_X_for_A";
            return false;
        }

        return true;
    }

    static bool ValidateCode(Span span, out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(span.XmlPath) || !string.IsNullOrWhiteSpace(span.Attr))
        {
            error = "mixed_axes";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Emit wire. Navigation → canonical names + Family.
    /// Code/xml → short aliases (compat) unless <paramref name="preferCanonical"/>.
    /// </summary>
    public static string Format(Span span, bool preferCanonical = false)
    {
        var family = ClassifyFamily(span, out _);
        var canon = preferCanonical || family == AxisFamily.Navigation
                    || !string.IsNullOrWhiteSpace(span.Family);

        var parts = new List<string>();
        var famName = family switch
        {
            AxisFamily.Navigation => "navigation",
            AxisFamily.Xml => "xml",
            AxisFamily.Csharp => "code",
            _ => NormalizeFamilyName(span.Family)
        };
        if (canon && !string.IsNullOrWhiteSpace(famName))
            parts.Add(Key("Family", canon) + ":" + famName);

        if (family == AxisFamily.Navigation)
        {
            if (!string.IsNullOrWhiteSpace(span.Command))
                parts.Add(Key("Command", canon) + ":" + span.Command.Trim());
            if (!string.IsNullOrWhiteSpace(span.Go))
                parts.Add(Key("Go", canon) + ":" + span.Go.Trim());
            if (span.NestedAnchor is { } nested)
                parts.Add("Anchor:" + Format(nested, preferCanonical: true));
            return "[" + string.Join(';', parts) + "]";
        }

        if (!string.IsNullOrWhiteSpace(span.File))
            parts.Add(Key("File", canon) + ":" + span.File.Trim());
        if (!string.IsNullOrWhiteSpace(span.MemberKey))
            parts.Add(Key("Member", canon) + ":" + span.MemberKey.Trim());
        if (span.LineStart is int ls)
        {
            var lineKey = Key("Line", canon);
            parts.Add(span.LineEnd is int le && le != ls
                ? $"{lineKey}:{ls}-{le}"
                : $"{lineKey}:{ls}");
        }

        if (!string.IsNullOrWhiteSpace(span.TextNeedle))
            parts.Add(Key("Text", canon) + ":" + SanitizeTextNeedle(span.TextNeedle));

        if (!string.IsNullOrWhiteSpace(span.ScopeKind))
        {
            var kind = span.ScopeKind.Trim().ToLowerInvariant();
            var idx = span.ScopeIndex is > 0 ? span.ScopeIndex.Value : 1;
            var scopeKey = Key("Scope", canon);
            parts.Add(idx == 1 ? $"{scopeKey}:{kind}" : $"{scopeKey}:{kind}:{idx}");
        }

        if (!string.IsNullOrWhiteSpace(span.XmlPath))
            parts.Add(Key("Element", canon) + ":" + span.XmlPath.Trim());
        if (!string.IsNullOrWhiteSpace(span.Attr))
            parts.Add(Key("Attribute", canon) + ":" + span.Attr.Trim());
        if (!string.IsNullOrWhiteSpace(span.Role))
            parts.Add(Key("Kind", canon) + ":" + span.Role.Trim());

        return "[" + string.Join(';', parts) + "]";
    }

    static string Key(string canonical, bool preferCanonical) => preferCanonical
        ? canonical
        : canonical switch
        {
            "Family" => "Family",
            "File" => "F",
            "Member" => "M",
            "Line" => "L",
            "Scope" => "S",
            "Text" => "T",
            "Kind" => "K",
            "Element" => "X",
            "Attribute" => "A",
            "Command" => "Command",
            "Go" => "Go",
            _ => canonical
        };

    static string StripOuterBrackets(string? bracketOrInner)
    {
        var text = (bracketOrInner ?? "").Trim();
        if (text.StartsWith('[') && text.EndsWith(']'))
            text = text[1..^1].Trim();
        return text;
    }

    /// <summary>Strip axis separators from content needle so wire stays parseable.</summary>
    public static string SanitizeTextNeedle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var s = raw.Trim().Replace("\r", "").Replace("\n", " ").Replace(";", " ");
        while (s.Contains("  ", StringComparison.Ordinal))
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        if (s.Length > 96)
            s = s[..96];
        return s.Trim();
    }

    static List<(string Key, string Value)> SplitAxes(string text)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(text))
            return list;

        foreach (var segment in SplitTopLevel(text, ';'))
        {
            var s = segment.Trim();
            if (s.Length == 0)
                continue;
            var colon = s.IndexOf(':');
            if (colon <= 0)
                throw new ArgumentException($"bad_axis_segment:{s}");
            list.Add((s[..colon].Trim(), s[(colon + 1)..].Trim()));
        }

        return list;
    }

    static List<string> SplitTopLevel(string text, char sep)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '[')
                depth++;
            else if (c == ']')
                depth = Math.Max(0, depth - 1);
            else if (c == sep && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    static void ParseLine(string val, out int? lineStart, out int? lineEnd)
    {
        lineStart = null;
        lineEnd = null;
        var dash = val.IndexOf('-');
        if (dash < 0)
        {
            if (int.TryParse(val.Trim(), out var one))
            {
                lineStart = one;
                lineEnd = one;
            }

            return;
        }

        if (int.TryParse(val[..dash].Trim(), out var a)
            && int.TryParse(val[(dash + 1)..].Trim(), out var b))
        {
            lineStart = a;
            lineEnd = b;
        }
    }

    static void ParseScope(string val, out string? scopeKind, out int? scopeIndex)
    {
        scopeKind = null;
        scopeIndex = null;
        var colon = val.IndexOf(':');
        if (colon < 0)
        {
            scopeKind = val.Trim().ToLowerInvariant();
            scopeIndex = 1;
            return;
        }

        scopeKind = val[..colon].Trim().ToLowerInvariant();
        scopeIndex = int.TryParse(val[(colon + 1)..].Trim(), out var idx) && idx > 0 ? idx : 1;
    }

    static string? NormalizeFamilyName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var v = raw.Trim().ToLowerInvariant();
        return v switch
        {
            "c#" or "csharp" or "cs" => "code",
            "nav" => "navigation",
            _ => v
        };
    }

    static bool IsTruthy(string val) =>
        val.Equals("true", StringComparison.OrdinalIgnoreCase)
        || val.Equals("1", StringComparison.OrdinalIgnoreCase)
        || val.Equals("yes", StringComparison.OrdinalIgnoreCase);
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

        // T: is parse-only unless we narrow here — silent ignore made place=after
        // insert at end of whole M: member (dogfood: IntentRouter RouteOne wiring).
        if (!string.IsNullOrWhiteSpace(span.TextNeedle))
        {
            if (!TryNarrowRangeToTextNeedle(target.Tree, target.Node, span.TextNeedle, out range, out var narrowDetail))
            {
                detail = narrowDetail;
                return false;
            }

            detail = $"{target.Detail}+T";
        }

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
        else if (!string.IsNullOrWhiteSpace(span.TextNeedle))
        {
            // T: alone: search whole compilation unit (M already narrowed searchRoot above).
            focus = searchRoot;
            resolveDetail = "file";
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

    /// <summary>
    /// Narrow a resolved syntax node range to the first <c>T:</c> needle match inside it.
    /// Needle is <see cref="BracketLocate.SanitizeTextNeedle"/> (same as wire parse).
    /// </summary>
    public static bool TryNarrowRangeToTextNeedle(
        SyntaxTree tree,
        SyntaxNode scope,
        string needleRaw,
        out TextRange range,
        out string detail)
    {
        range = default!;
        var needle = BracketLocate.SanitizeTextNeedle(needleRaw);
        if (needle.Length == 0)
        {
            detail = "text_needle_empty";
            return false;
        }

        var source = tree.GetText();
        var nodeSpan = scope.Span;
        var haystack = source.ToString(nodeSpan);
        if (!TryFindNeedleOffset(haystack, needle, out var relStart, out var matchLen))
        {
            detail = "text_needle_not_found";
            return false;
        }

        var absStart = nodeSpan.Start + relStart;
        var absEnd = absStart + matchLen;
        // SanitizeTextNeedle strips ';' from the wire value, but source still has
        // statement terminators — extend so place=after does not land between
        // `return "x"` and `;` (would split the statement).
        var fullText = source.ToString();
        if (absEnd < fullText.Length && fullText[absEnd] == ';')
            absEnd++;
        var startPos = source.Lines.GetLinePosition(absStart);
        var endPos = source.Lines.GetLinePosition(absEnd);
        range = new TextRange(
            startPos.Line + 1,
            startPos.Character + 1,
            endPos.Line + 1,
            Math.Max(1, endPos.Character + 1));
        detail = "T";
        return true;
    }

    /// <summary>
    /// Insert edges for block-interior places (type/ns before|after, or method into|end):
    /// inside the braces, not outside the declaration.
    /// Method <c>M:</c>+before|after stays sibling-outside at the DocumentEditPlane layer.
    /// Returns a zero-width <see cref="TextRange"/> at the insert point.
    /// </summary>
    public static bool TryGetBlockInteriorInsertPoint(
        SyntaxNode node,
        bool before,
        out TextRange point,
        out string detail)
    {
        point = default!;
        detail = "";

        SyntaxToken open;
        SyntaxToken close;
        switch (node)
        {
            case BlockSyntax block:
                open = block.OpenBraceToken;
                close = block.CloseBraceToken;
                break;
            case MethodDeclarationSyntax { Body: { } methodBody }:
                open = methodBody.OpenBraceToken;
                close = methodBody.CloseBraceToken;
                break;
            case ConstructorDeclarationSyntax { Body: { } ctorBody }:
                open = ctorBody.OpenBraceToken;
                close = ctorBody.CloseBraceToken;
                break;
            case DestructorDeclarationSyntax { Body: { } dtorBody }:
                open = dtorBody.OpenBraceToken;
                close = dtorBody.CloseBraceToken;
                break;
            case OperatorDeclarationSyntax { Body: { } opBody }:
                open = opBody.OpenBraceToken;
                close = opBody.CloseBraceToken;
                break;
            case LocalFunctionStatementSyntax { Body: { } localBody }:
                open = localBody.OpenBraceToken;
                close = localBody.CloseBraceToken;
                break;
            case AccessorDeclarationSyntax { Body: { } accBody }:
                open = accBody.OpenBraceToken;
                close = accBody.CloseBraceToken;
                break;
            case TypeDeclarationSyntax type
                when !type.OpenBraceToken.IsKind(SyntaxKind.None)
                     && !type.CloseBraceToken.IsKind(SyntaxKind.None):
                open = type.OpenBraceToken;
                close = type.CloseBraceToken;
                break;
            case NamespaceDeclarationSyntax ns:
                open = ns.OpenBraceToken;
                close = ns.CloseBraceToken;
                break;
            default:
                if (TryGetExpressionBodyExpression(node, out var expr))
                    return PointAtNodeEdge(expr, before, "expression_body_edge", out point, out detail);
                detail = "no_block_body";
                return false;
        }

        if (open.IsKind(SyntaxKind.None) || close.IsKind(SyntaxKind.None))
        {
            detail = "no_block_body";
            return false;
        }

        var tree = node.SyntaxTree;
        if (tree is null)
        {
            detail = "no_syntax_tree";
            return false;
        }

        var source = tree.GetText();
        var abs = before ? open.Span.End : close.Span.Start;
        var pos = source.Lines.GetLinePosition(abs);
        point = new TextRange(
            pos.Line + 1,
            pos.Character + 1,
            pos.Line + 1,
            pos.Character + 1);
        detail = before ? "block_body_start" : "block_body_end";
        return true;
    }

    static bool TryGetExpressionBodyExpression(SyntaxNode node, out ExpressionSyntax expr)
    {
        expr = node switch
        {
            MethodDeclarationSyntax { ExpressionBody.Expression: { } e } => e,
            LocalFunctionStatementSyntax { ExpressionBody.Expression: { } e } => e,
            OperatorDeclarationSyntax { ExpressionBody.Expression: { } e } => e,
            AccessorDeclarationSyntax { ExpressionBody.Expression: { } e } => e,
            PropertyDeclarationSyntax { ExpressionBody.Expression: { } e } => e,
            _ => null!
        };
        return expr is not null;
    }

    static bool PointAtNodeEdge(
        SyntaxNode node,
        bool before,
        string detailName,
        out TextRange point,
        out string detail)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        if (before)
        {
            point = new TextRange(
                lineSpan.StartLinePosition.Line + 1,
                lineSpan.StartLinePosition.Character + 1,
                lineSpan.StartLinePosition.Line + 1,
                lineSpan.StartLinePosition.Character + 1);
        }
        else
        {
            point = new TextRange(
                lineSpan.EndLinePosition.Line + 1,
                Math.Max(1, lineSpan.EndLinePosition.Character + 1),
                lineSpan.EndLinePosition.Line + 1,
                Math.Max(1, lineSpan.EndLinePosition.Character + 1));
        }

        detail = detailName;
        return true;
    }

    /// <summary>
    /// Ordinal match first; if sanitize collapsed whitespace, match collapsed haystack and map back.
    /// </summary>
    static bool TryFindNeedleOffset(string haystack, string needle, out int start, out int length)
    {
        start = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (start >= 0)
        {
            length = needle.Length;
            return true;
        }

        var map = new List<int>(haystack.Length);
        var collapsed = new System.Text.StringBuilder(haystack.Length);
        for (var i = 0; i < haystack.Length; i++)
        {
            var c = haystack[i];
            if (char.IsWhiteSpace(c))
            {
                if (collapsed.Length > 0 && collapsed[^1] != ' ')
                {
                    collapsed.Append(' ');
                    map.Add(i);
                }

                continue;
            }

            collapsed.Append(c);
            map.Add(i);
        }

        var cHay = collapsed.ToString();
        var cIdx = cHay.IndexOf(needle, StringComparison.Ordinal);
        if (cIdx < 0 || cIdx + needle.Length > map.Count)
        {
            length = 0;
            return false;
        }

        start = map[cIdx];
        var endInclusive = map[cIdx + needle.Length - 1];
        length = endInclusive - start + 1;
        return true;
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
