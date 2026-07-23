using System.Net;
using System.Text;

namespace Cdp.ScriptableIde;

/// <summary>C# XML doc projection (<c>///</c>). Other languages = later projections.</summary>
public static class CsharpXmlDocProjection
{
    /// <summary>Returns lines each starting with <c>///</c> (no trailing indent).</summary>
    public static string Format(DocModel model, out string[] warnings)
    {
        var warn = new List<string>();
        if (!string.IsNullOrWhiteSpace(model.Raw))
        {
            warnings = [];
            return ToDocCommentLines(model.Raw!);
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(model.Summary))
        {
            sb.AppendLine("/// <summary>");
            foreach (var line in SplitLines(model.Summary!))
                sb.Append("/// ").AppendLine(Xml(line));
            sb.AppendLine("/// </summary>");
        }

        foreach (var p in model.Params)
        {
            sb.Append("/// <param name=\"").Append(XmlAttr(p.Name)).Append("\">")
                .Append(Xml(p.Text)).AppendLine("</param>");
        }

        if (!string.IsNullOrWhiteSpace(model.Returns))
        {
            sb.Append("/// <returns>").Append(Xml(model.Returns!)).AppendLine("</returns>");
        }

        foreach (var t in model.Throws)
        {
            if (string.IsNullOrWhiteSpace(t.Type))
            {
                sb.Append("/// <exception>").Append(Xml(t.Text)).AppendLine("</exception>");
            }
            else
            {
                sb.Append("/// <exception cref=\"").Append(XmlAttr(t.Type!)).Append("\">")
                    .Append(Xml(t.Text)).AppendLine("</exception>");
            }
        }

        var remarks = model.Remarks;
        if (!string.IsNullOrWhiteSpace(model.Examples))
        {
            remarks = string.IsNullOrWhiteSpace(remarks)
                ? model.Examples
                : remarks + "\n" + model.Examples;
            warn.Add("examples_folded_into_remarks");
        }

        if (!string.IsNullOrWhiteSpace(remarks))
        {
            sb.AppendLine("/// <remarks>");
            foreach (var line in SplitLines(remarks!))
                sb.Append("/// ").AppendLine(Xml(line));
            sb.AppendLine("/// </remarks>");
        }

        foreach (var see in model.SeeAlso)
        {
            sb.Append("/// <seealso cref=\"").Append(XmlAttr(see)).AppendLine("\" />");
        }

        warnings = warn.ToArray();
        var text = sb.ToString().TrimEnd() + "\n";
        return text;
    }

    private static string ToDocCommentLines(string raw)
    {
        var sb = new StringBuilder();
        foreach (var line in SplitLines(raw))
        {
            if (line.StartsWith("///", StringComparison.Ordinal))
                sb.AppendLine(line);
            else
                sb.Append("/// ").AppendLine(line);
        }
        return sb.ToString();
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.None);

    private static string Xml(string s) => WebUtility.HtmlEncode(s);

    private static string XmlAttr(string s) => WebUtility.HtmlEncode(s);
}
