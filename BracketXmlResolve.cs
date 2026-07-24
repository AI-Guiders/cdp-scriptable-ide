using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cdp.ScriptableIde;

/// <summary>
/// Resolve <c>X:</c>/<c>A:</c> to a 1-based text range (MSBuild-ish XML, no namespaces).
/// Segments: <c>Name</c>, <c>Name@Attr=value</c>, <c>Name:2</c> (1-based among matching siblings).
/// <c>K:Element</c> — if leaf missing, insert under parent (zero-width range + insert name).
/// </summary>
public static class BracketXmlResolve
{
    public sealed record TextRange(int LineStart, int ColumnStart, int LineEnd, int ColumnEnd);

    public sealed record ResolveResult(
        TextRange Range,
        string Detail,
        bool Insert,
        string? InsertElementName,
        string? InsertIndent);

    static readonly Regex SegmentRx = new(
        @"^(?<name>[A-Za-z_][\w.-]*)(?:@(?<attr>[A-Za-z_][\w.-]*)=(?<val>[^:]+))?(?::(?<index>\d+))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryResolve(
        string absoluteFilePath,
        string? sourceText,
        BracketLocate.Span span,
        out ResolveResult result,
        out string detail)
    {
        result = default!;
        detail = "";

        if (string.IsNullOrWhiteSpace(span.XmlPath))
        {
            detail = "need_X";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(span.MemberKey) || !string.IsNullOrWhiteSpace(span.ScopeKind))
        {
            detail = "mixed_axes";
            return false;
        }

        string text;
        if (sourceText is not null)
            text = sourceText;
        else if (File.Exists(absoluteFilePath))
            text = File.ReadAllText(absoluteFilePath);
        else
        {
            detail = "file_missing";
            return false;
        }

        var segments = span.XmlPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            detail = "empty_X";
            return false;
        }

        var path = new List<Segment>(segments.Length);
        foreach (var raw in segments)
        {
            if (!TryParseSegment(raw, out var seg, out detail))
                return false;
            path.Add(seg);
        }

        var wantAttr = string.IsNullOrWhiteSpace(span.Attr) ? null : span.Attr.Trim();
        var upsert = span.Role is not null
            && span.Role.Equals("Element", StringComparison.OrdinalIgnoreCase);

        if (wantAttr is not null
            && span.Role is not null
            && !span.Role.Equals("Attr", StringComparison.OrdinalIgnoreCase)
            && !span.Role.Equals("Attribute", StringComparison.OrdinalIgnoreCase)
            && !span.Role.Equals("Element", StringComparison.OrdinalIgnoreCase))
        {
            detail = "axis_not_consumed:K";
            return false;
        }

        if (!TryWalk(text, path, out var hit, out detail))
        {
            if (!upsert || wantAttr is not null)
                return false;
            if (path.Count < 2)
            {
                detail = "element_not_found_need_parent";
                return false;
            }

            var parentPath = path.Take(path.Count - 1).ToList();
            if (!TryWalk(text, parentPath, out var parent, out detail))
                return false;
            if (parent.IsEmpty || parent.EndTagStart < 0)
            {
                detail = "parent_not_container";
                return false;
            }

            OffsetToLineCol(text, parent.EndTagStart, out var il, out var ic);
            var indent = GuessIndent(text, parent.EndTagStart);
            result = new ResolveResult(
                new TextRange(il, ic, il, ic),
                "xml_insert_element",
                Insert: true,
                InsertElementName: path[^1].Name,
                InsertIndent: indent);
            detail = result.Detail;
            return true;
        }

        if (wantAttr is not null)
        {
            if (!TryAttrValueRange(text, hit.StartTagOpen, hit.StartTagEnd, wantAttr, out var ar, out detail))
                return false;
            result = new ResolveResult(ar, "xml_attr", false, null, null);
            detail = result.Detail;
            return true;
        }

        if (hit.IsEmpty)
        {
            detail = "empty_element_no_text";
            return false;
        }

        OffsetToLineCol(text, hit.ContentStart, out var ls, out var cs);
        OffsetToLineCol(text, hit.ContentEnd, out var le, out var ce);
        result = new ResolveResult(new TextRange(ls, cs, le, ce), "xml_text", false, null, null);
        detail = result.Detail;
        return true;
    }

    public static string BuildInsertElement(string elementName, string innerText, string indent) =>
        $"{indent}<{elementName}>{innerText}</{elementName}>\n";

    sealed record Segment(string Name, string? FilterAttr, string? FilterValue, int Index);

    sealed record Hit(
        int StartTagOpen,
        int StartTagEnd,
        int ContentStart,
        int ContentEnd,
        int EndTagStart,
        bool IsEmpty);

    static bool TryParseSegment(string raw, out Segment seg, out string detail)
    {
        seg = default!;
        detail = "";
        var m = SegmentRx.Match(raw.Trim());
        if (!m.Success)
        {
            detail = $"bad_X_segment:{raw}";
            return false;
        }

        var name = m.Groups["name"].Value;
        string? attr = m.Groups["attr"].Success ? m.Groups["attr"].Value : null;
        string? val = m.Groups["val"].Success ? m.Groups["val"].Value.Trim() : null;
        var index = m.Groups["index"].Success
            ? int.Parse(m.Groups["index"].Value, CultureInfo.InvariantCulture)
            : 1;
        if (index < 1)
        {
            detail = "segment_index_lt_1";
            return false;
        }

        seg = new Segment(name, attr, val, index);
        return true;
    }

    static bool TryWalk(string text, IReadOnlyList<Segment> path, out Hit hit, out string detail)
    {
        hit = default!;
        detail = "";
        // Sibling counters per path depth while descending.
        var counts = new int[path.Count];
        var depth = 0; // path depth matched so far
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '<')
            {
                i++;
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '!')
            {
                // comment or cdata — skip
                if (StartsWith(text, i, "<!--"))
                {
                    var end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 3;
                    continue;
                }

                i++;
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '?')
            {
                var end = text.IndexOf("?>", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '/')
            {
                // end tag — pop path depth if closing the element we entered
                if (!TryReadName(text, i + 2, out var endName, out var afterName))
                {
                    i++;
                    continue;
                }

                var gt = text.IndexOf('>', afterName);
                if (gt < 0)
                    break;
                if (depth > 0 && endName.Equals(path[depth - 1].Name, StringComparison.Ordinal))
                {
                    // closing current matched ancestor
                    depth--;
                    for (var c = depth; c < path.Count; c++)
                        counts[c] = 0;
                }

                i = gt + 1;
                continue;
            }

            // start tag
            if (!TryReadName(text, i + 1, out var name, out var nameEnd))
            {
                i++;
                continue;
            }

            if (!TryFindStartTagEnd(text, i, out var tagEnd, out var empty))
            {
                detail = "malformed_tag";
                return false;
            }

            var tagInner = text[(nameEnd)..(empty ? tagEnd - 2 : tagEnd - 1)];

            if (depth < path.Count)
            {
                var want = path[depth];
                if (name.Equals(want.Name, StringComparison.Ordinal)
                    && AttrFilterOk(tagInner, want))
                {
                    counts[depth]++;
                    if (counts[depth] == want.Index)
                    {
                        if (depth == path.Count - 1)
                        {
                            if (empty)
                            {
                                hit = new Hit(i, tagEnd, tagEnd, tagEnd, -1, true);
                                return true;
                            }

                            var contentStart = tagEnd;
                            if (!TryFindMatchingEnd(text, name, contentStart, out var contentEnd, out var endTagStart))
                            {
                                detail = "end_tag_not_found";
                                return false;
                            }

                            hit = new Hit(i, tagEnd, contentStart, contentEnd, endTagStart, false);
                            return true;
                        }

                        depth++;
                        for (var c = depth; c < path.Count; c++)
                            counts[c] = 0;
                        i = tagEnd;
                        if (empty)
                        {
                            // entered then immediately left empty — shouldn't advance path on empty for non-leaf
                            depth--;
                        }

                        continue;
                    }
                }
            }

            if (!empty)
            {
                // Skip whole element if not descending into it for path
                if (!TryFindMatchingEnd(text, name, tagEnd, out _, out var endTag))
                {
                    i = tagEnd;
                    continue;
                }

                var closeEnd = text.IndexOf('>', endTag);
                i = closeEnd < 0 ? text.Length : closeEnd + 1;
                continue;
            }

            i = tagEnd;
        }

        detail = "element_not_found";
        return false;
    }

    static bool AttrFilterOk(string tagInner, Segment want)
    {
        if (want.FilterAttr is null)
            return true;
        var rx = new Regex(
            $@"\b{Regex.Escape(want.FilterAttr)}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')",
            RegexOptions.CultureInvariant);
        var m = rx.Match(tagInner);
        return m.Success && m.Groups["v"].Value.Equals(want.FilterValue, StringComparison.Ordinal);
    }

    static bool TryAttrValueRange(
        string text,
        int startTagOpen,
        int startTagEnd,
        string attrName,
        out TextRange range,
        out string detail)
    {
        range = default!;
        detail = "";
        var slice = text[startTagOpen..startTagEnd];
        var rx = new Regex(
            $@"\b{Regex.Escape(attrName)}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')",
            RegexOptions.CultureInvariant);
        var m = rx.Match(slice);
        if (!m.Success)
        {
            detail = $"attr_not_found:{attrName}";
            return false;
        }

        var g = m.Groups["v"];
        var absStart = startTagOpen + g.Index;
        var absEnd = absStart + g.Length;
        OffsetToLineCol(text, absStart, out var ls, out var cs);
        OffsetToLineCol(text, absEnd, out var le, out var ce);
        range = new TextRange(ls, cs, le, ce);
        return true;
    }

    static bool TryReadName(string text, int start, out string name, out int after)
    {
        name = "";
        after = start;
        if (start >= text.Length || !IsNameStart(text[start]))
            return false;
        var i = start + 1;
        while (i < text.Length && IsNameContinue(text[i]))
            i++;
        name = text[start..i];
        after = i;
        return true;
    }

    static bool IsNameStart(char c) => char.IsLetter(c) || c == '_';
    static bool IsNameContinue(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.';

    static bool StartsWith(string text, int i, string s) =>
        i + s.Length <= text.Length && string.CompareOrdinal(text, i, s, 0, s.Length) == 0;

    static bool TryFindStartTagEnd(string text, int startTagOpen, out int endExclusive, out bool empty)
    {
        empty = false;
        endExclusive = -1;
        var i = startTagOpen + 1;
        var inQuote = '\0';
        while (i < text.Length)
        {
            var c = text[i];
            if (inQuote != '\0')
            {
                if (c == inQuote)
                    inQuote = '\0';
                i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                inQuote = c;
                i++;
                continue;
            }

            if (c == '>')
            {
                empty = i > startTagOpen + 1 && text[i - 1] == '/';
                endExclusive = i + 1;
                return true;
            }

            i++;
        }

        return false;
    }

    static bool TryFindMatchingEnd(
        string text,
        string name,
        int contentStart,
        out int contentEnd,
        out int endTagStart)
    {
        contentEnd = contentStart;
        endTagStart = -1;
        var depth = 0;
        var i = contentStart;
        var open = "<" + name;
        var close = "</" + name;
        while (i < text.Length)
        {
            if (text[i] != '<')
            {
                i++;
                continue;
            }

            if (StartsWith(text, i, close)
                && (i + close.Length >= text.Length || !IsNameContinue(text[i + close.Length])))
            {
                if (depth == 0)
                {
                    contentEnd = i;
                    endTagStart = i;
                    return true;
                }

                depth--;
                i += close.Length;
                continue;
            }

            if (StartsWith(text, i, open)
                && (i + open.Length >= text.Length || !IsNameContinue(text[i + open.Length])))
            {
                if (!TryFindStartTagEnd(text, i, out var tagEnd, out var empty))
                    return false;
                if (!empty)
                    depth++;
                i = tagEnd;
                continue;
            }

            // other tag — skip
            if (i + 1 < text.Length && text[i + 1] == '/')
            {
                var gt = text.IndexOf('>', i + 2);
                i = gt < 0 ? text.Length : gt + 1;
                continue;
            }

            if (!TryFindStartTagEnd(text, i, out var otherEnd, out _))
                return false;
            i = otherEnd;
        }

        return false;
    }

    static string GuessIndent(string text, int endTagOffset)
    {
        var lineStart = endTagOffset;
        while (lineStart > 0 && text[lineStart - 1] != '\n')
            lineStart--;
        var sb = new StringBuilder();
        for (var i = lineStart; i < endTagOffset; i++)
        {
            if (text[i] is ' ' or '\t')
                sb.Append(text[i]);
            else
                break;
        }

        return sb.Length > 0 ? sb + "  " : "  ";
    }

    public static void OffsetToLineCol(string text, int offset, out int line, out int column)
    {
        line = 1;
        column = 1;
        var o = Math.Clamp(offset, 0, text.Length);
        for (var i = 0; i < o; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
                column++;
        }
    }
}
