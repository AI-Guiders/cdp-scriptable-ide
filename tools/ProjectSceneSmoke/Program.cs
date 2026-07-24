using Cdp.ScriptableIde;

var sample = """
These templates matched your input: --type='project'

Template Name                     Short Name                          Language    Tags
--------------------------------  ----------------------------------  ----------  -------------
Class Library                     classlib                            [C#],F#,VB  Common/Library
Console App                       console                             [C#],F#,VB  Common/Console
ASP.NET Core Web App (Razor P...  webapp,razor                        [C#]        Web/MVC/Razor Pages
""";

var cards = ProjectScene.ParseDotnetNewList(sample, 80);
if (cards.Count < 3 || cards.All(c => c.Id != "classlib") || cards.All(c => c.Id != "webapp"))
{
    Console.Error.WriteLine("FAIL parse: " + string.Join(",", cards.Select(c => c.Id)));
    return 1;
}

var cols = ProjectScene.SplitColumns("Class Library                     classlib                            [C#],F#,VB  Common/Library");
if (cols.Count < 2 || cols[1] != "classlib")
{
    Console.Error.WriteLine("FAIL cols: " + string.Join("|", cols));
    return 2;
}

Console.WriteLine("OK project_scene parse " + cards.Count);
return 0;
