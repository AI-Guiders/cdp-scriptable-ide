using System.Text.RegularExpressions;

namespace Cdp.ScriptableIde;

/// <summary>
/// Shared zone → get_code_actions → title pick → apply (+ optional action_options / constant_name).
/// </summary>
internal static partial class RefactorCodeActionRunner
{
    [GeneratedRegex(@"^(?<idx>\d+)\t(?<title>.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ActionLine();

    public sealed record TitlePick(string Contains, string? ExcludeContains = null);

    public static async Task<StepResponse> RunAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string kind,
        string anchorTarget,
        IReadOnlyList<TitlePick> titlePicks,
        string? tillTarget = null,
        string? preferredName = null,
        string? constantName = null,
        object? actionOptions = null,
        CancellationToken ct = default)
    {
        if (titlePicks.Count == 0)
            return StepResponse.Fail(kind, "title pick predicates required");

        if (!AnchorLocus.TryResolveTextRange(plan, anchorTarget, kind, out var file, out var fromRange, out var fail))
            return fail!;

        BracketSyntaxResolve.TextRange zone;
        if (string.IsNullOrWhiteSpace(tillTarget) || string.Equals(tillTarget, anchorTarget, StringComparison.Ordinal))
        {
            zone = fromRange;
        }
        else
        {
            if (!AnchorLocus.TryResolveTextRange(plan, tillTarget!, kind, out var tillFile, out var tillRange, out fail))
                return fail!;
            if (!string.Equals(file, tillFile, StringComparison.OrdinalIgnoreCase))
                return StepResponse.Fail(kind, "From/Till must be in the same file", new { from = file, till = tillFile });
            if (!AnchorLocus.TryMergeZones(fromRange, tillRange, kind, out zone, out fail))
                return fail!;
        }

        var sol = AnchorLocus.RequireSolution(plan, kind, out fail);
        if (fail is not null)
            return fail;

        var listRaw = await bus.InvokeAsync("roslyn", "roslyn_get_code_actions", ScriptArgs.From(new
        {
            solution_or_project_path = sol,
            file_path = file,
            line = zone.LineStart,
            column = zone.ColumnStart,
            end_line = zone.LineEnd,
            end_column = zone.ColumnEnd
        }), ct).ConfigureAwait(false);

        var actions = ParseActions(listRaw);
        if (actions.Count == 0)
            return StepResponse.Fail(kind, "no code actions at locus", new { file, zone, list = listRaw });

        if (!TryPick(actions, titlePicks, out var actionIndex, out var title))
        {
            return StepResponse.Fail(kind, "no matching code action", new
            {
                picks = titlePicks.Select(p => new { p.Contains, p.ExcludeContains }),
                actions = actions.Select(a => new { a.Index, a.Title })
            });
        }

        object applyArgs = BuildApplyArgs(sol, file, zone, actionIndex, constantName, actionOptions);
        var applyRaw = await bus.InvokeAsync("roslyn", "roslyn_apply_code_action", ScriptArgs.From(applyArgs), ct)
            .ConfigureAwait(false);

        StepResponse? renameStep = null;
        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            // Introduce local/param often leaves a default identifier — rename when found.
            foreach (var candidate in new[] { "v", "V", "local", "value", "p", "param" })
            {
                if (!TryFindIdentifierNear(file, candidate, zone.LineStart, out var rLine, out var rCol))
                    continue;
                var renameRaw = await bus.InvokeAsync("roslyn", "roslyn_rename", ScriptArgs.From(new
                {
                    solution_or_project_path = sol,
                    file_path = file,
                    line = rLine,
                    column = rCol,
                    new_name = preferredName,
                    apply = true
                }), ct).ConfigureAwait(false);
                renameStep = StepResponse.ParseOrWrap(renameRaw, "roslyn.rename");
                break;
            }
        }

        return StepResponse.Success(kind, $"Applied: {title}", new
        {
            anchor = anchorTarget,
            till = tillTarget ?? anchorTarget,
            zone = new { zone.LineStart, zone.ColumnStart, zone.LineEnd, zone.ColumnEnd },
            action_title = title,
            action_index = actionIndex,
            name = preferredName,
            apply = FixRunner.NormalizeRoslynMutate(applyRaw, "roslyn.apply_code_action"),
            rename = renameStep
        });
    }

    private static object BuildApplyArgs(
        string sol,
        string file,
        BracketSyntaxResolve.TextRange zone,
        int actionIndex,
        string? constantName,
        object? actionOptions)
    {
        if (actionOptions is not null && !string.IsNullOrWhiteSpace(constantName))
        {
            return new
            {
                solution_or_project_path = sol,
                file_path = file,
                line = zone.LineStart,
                column = zone.ColumnStart,
                end_line = zone.LineEnd,
                end_column = zone.ColumnEnd,
                action_index = actionIndex,
                constant_name = constantName,
                action_options = actionOptions
            };
        }

        if (actionOptions is not null)
        {
            return new
            {
                solution_or_project_path = sol,
                file_path = file,
                line = zone.LineStart,
                column = zone.ColumnStart,
                end_line = zone.LineEnd,
                end_column = zone.ColumnEnd,
                action_index = actionIndex,
                action_options = actionOptions
            };
        }

        if (!string.IsNullOrWhiteSpace(constantName))
        {
            return new
            {
                solution_or_project_path = sol,
                file_path = file,
                line = zone.LineStart,
                column = zone.ColumnStart,
                end_line = zone.LineEnd,
                end_column = zone.ColumnEnd,
                action_index = actionIndex,
                constant_name = constantName
            };
        }

        return new
        {
            solution_or_project_path = sol,
            file_path = file,
            line = zone.LineStart,
            column = zone.ColumnStart,
            end_line = zone.LineEnd,
            end_column = zone.ColumnEnd,
            action_index = actionIndex
        };
    }

    private static bool TryPick(
        IReadOnlyList<(int Index, string Title)> actions,
        IReadOnlyList<TitlePick> picks,
        out int index,
        out string title)
    {
        index = -1;
        title = "";
        foreach (var pick in picks)
        {
            foreach (var a in actions)
            {
                if (!a.Title.Contains(pick.Contains, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrWhiteSpace(pick.ExcludeContains)
                    && a.Title.Contains(pick.ExcludeContains!, StringComparison.OrdinalIgnoreCase))
                    continue;
                index = a.Index;
                title = a.Title;
                return true;
            }
        }

        return false;
    }

    private static List<(int Index, string Title)> ParseActions(string listRaw)
    {
        var list = new List<(int, string)>();
        var step = StepResponse.ParseOrWrap(listRaw, "roslyn.get_code_actions");
        if (step.Ok && step.Data is { } data
            && data.TryGetProperty("actions", out var actions)
            && actions.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var a in actions.EnumerateArray())
            {
                var t = a.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
                var idx = a.TryGetProperty("index", out var idxEl) && idxEl.TryGetInt32(out var i) ? i : -1;
                if (idx >= 0 && t.Length > 0)
                    list.Add((idx, t));
            }

            if (list.Count > 0)
                return list;
        }

        foreach (Match m in ActionLine().Matches(listRaw))
            list.Add((int.Parse(m.Groups["idx"].Value), m.Groups["title"].Value.Trim()));
        return list;
    }

    private static bool TryFindIdentifierNear(string file, string name, int nearLine, out int line, out int column)
    {
        line = 0;
        column = 0;
        try
        {
            var lines = File.ReadAllLines(file);
            var start = Math.Clamp(nearLine - 1, 0, lines.Length - 1);
            for (var dist = 0; dist < lines.Length; dist++)
            {
                foreach (var i in new[] { start + dist, start - dist }.Distinct())
                {
                    if (i < 0 || i >= lines.Length)
                        continue;
                    var idx = lines[i].IndexOf(name, StringComparison.Ordinal);
                    if (idx < 0)
                        continue;
                    // Prefer declaration-ish contexts
                    if (lines[i].Contains("var " + name, StringComparison.Ordinal)
                        || lines[i].Contains(" " + name + " =", StringComparison.Ordinal)
                        || lines[i].Contains("(" + name, StringComparison.Ordinal)
                        || lines[i].Contains(", " + name, StringComparison.Ordinal))
                    {
                        line = i + 1;
                        column = idx + 1;
                        return true;
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
