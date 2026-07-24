using System.Text.Json;
using Cdp.ScriptableIde;

// Help.Toc/Of from XML + CheckAsync DiagnosticItems with anchors.
var tocJson = CsxHelpCatalog.Toc();
using (var toc = JsonDocument.Parse(tocJson))
{
    var root = toc.RootElement;
    if (!root.GetProperty("ok").GetBoolean())
    {
        Console.Error.WriteLine("toc not ok: " + tocJson);
        return 1;
    }

    var names = root.GetProperty("facades").EnumerateArray()
        .Select(f => f.GetProperty("name").GetString())
        .ToArray();
    if (!names.Contains("Help", StringComparer.Ordinal)
        || !names.Contains("Symbol", StringComparer.Ordinal)
        || !names.Contains("SemanticMap", StringComparer.Ordinal))
    {
        Console.Error.WriteLine("toc missing Help/Symbol/SemanticMap: " + string.Join(",", names));
        return 2;
    }
}

var ofJson = CsxHelpCatalog.Of("Symbol");
using (var of = JsonDocument.Parse(ofJson))
{
    var members = of.RootElement.GetProperty("members").EnumerateArray()
        .Select(m => m.GetProperty("name").GetString())
        .ToArray();
    if (!members.Contains("Named", StringComparer.Ordinal))
    {
        Console.Error.WriteLine("Symbol.Of missing Named: " + ofJson);
        return 3;
    }
}

var bad = await ScriptHost.CheckAsync("""
return await Symbol.SearchAsync("Nope");
""");
if (bad.Ok || bad.DiagnosticItems is not { Count: > 0 } items)
{
    Console.Error.WriteLine("expected invent-API errors, got ok=" + bad.Ok + " err=" + bad.Error);
    return 4;
}

var first = items[0];
if (string.IsNullOrWhiteSpace(first.Anchor) || !first.Anchor.Contains("<csx>", StringComparison.Ordinal))
{
    Console.Error.WriteLine("expected <csx> anchor, got: " + first.Anchor + " / " + first.Message);
    return 5;
}

if (bad.Diagnostics is null || !bad.Diagnostics[0].Contains(first.Anchor, StringComparison.Ordinal))
{
    Console.Error.WriteLine("legacy Diagnostics missing anchor: " + bad.Diagnostics?[0]);
    return 6;
}

Console.WriteLine("OK help+diag anchor=" + first.Anchor + " id=" + first.Id);
return 0;
