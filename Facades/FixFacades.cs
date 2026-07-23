using System.Text.RegularExpressions;

namespace Cdp.ScriptableIde;

/// <summary>Fix intents — diag locus → code action (MLP W1).</summary>
public sealed class FixFacade(IScriptToolBus bus, PlanContext plan)
{
    /// <summary>Primary: fix at Anchor locus (resolves caret like Rename).</summary>
    public FixAt At(Anchor anchor) => At(anchor.ToWire());

    public FixAt At(BracketLocate.Span span) => At(BracketLocate.Format(span));

    /// <summary>Primary wire: bracket Anchor string.</summary>
    public FixAt At(string anchorOrDiagnosticLine)
    {
        if (IdeDiagnostic.TryParse(anchorOrDiagnosticLine, out var diag))
            return At(diag);

        if (anchorOrDiagnosticLine.Contains('[') || anchorOrDiagnosticLine.Contains("F:", StringComparison.Ordinal))
        {
            if (!AnchorLocus.TryResolveCaret(plan, anchorOrDiagnosticLine, FixRunner.Kind, out var file, out var line,
                    out var column, out var fail))
            {
                return new FixAt(bus, plan, null, null, null, null, fail!);
            }

            return new FixAt(bus, plan, file, line, column, diagnosticId: null, seedFail: null);
        }

        return new FixAt(bus, plan, null, null, null, null,
            StepResponse.Fail(FixRunner.Kind, "unparseable diagnostic line or Anchor", new { line = anchorOrDiagnosticLine }));
    }

    public FixAt At(IdeDiagnostic diagnostic) =>
        new(bus, plan, diagnostic.FilePath, diagnostic.Line, diagnostic.Column, diagnostic.Id, seedFail: null);

    /// <summary>Escape: bare file/line/column.</summary>
    public FixAt At(string filePath, int line, int column, string? diagnosticId = null) =>
        new(bus, plan, filePath, line, column, diagnosticId, seedFail: null);

    public FixAllFacade All => new(bus, plan);
}

public sealed class FixAllFacade(IScriptToolBus bus, PlanContext plan)
{
    public FixAt Document(string diagnosticLine) => Scope(diagnosticLine, document: true);

    public FixAt Document(IdeDiagnostic diagnostic) =>
        new FixAt(bus, plan, diagnostic.FilePath, diagnostic.Line, diagnostic.Column, diagnostic.Id, seedFail: null)
            .AllDocument();

    public FixAt Document(string filePath, int line, int column, string? diagnosticId = null) =>
        new FixAt(bus, plan, filePath, line, column, diagnosticId, seedFail: null).AllDocument();

    public FixAt Project(string diagnosticLine) => Scope(diagnosticLine, document: false);

    public FixAt Project(IdeDiagnostic diagnostic) =>
        new FixAt(bus, plan, diagnostic.FilePath, diagnostic.Line, diagnostic.Column, diagnostic.Id, seedFail: null)
            .AllProject();

    public FixAt Project(string filePath, int line, int column, string? diagnosticId = null) =>
        new FixAt(bus, plan, filePath, line, column, diagnosticId, seedFail: null).AllProject();

    private FixAt Scope(string diagnosticLine, bool document)
    {
        if (!IdeDiagnostic.TryParse(diagnosticLine, out var diag))
            return new FixAt(bus, plan, null, null, null, null,
                StepResponse.Fail(FixRunner.Kind, "unparseable diagnostic line", new { line = diagnosticLine }));
        var at = new FixAt(bus, plan, diag.FilePath, diag.Line, diag.Column, diag.Id, seedFail: null);
        return document ? at.AllDocument() : at.AllProject();
    }
}

public sealed class FixAt
{
    private readonly IScriptToolBus _bus;
    private readonly PlanContext _plan;
    private readonly string? _file;
    private readonly int? _line;
    private readonly int? _column;
    private readonly string? _diagnosticId;
    private readonly StepResponse? _seedFail;
    private string? _titleContains;
    private int? _actionIndex;
    private string? _fixAllScope;

    internal FixAt(
        IScriptToolBus bus,
        PlanContext plan,
        string? file,
        int? line,
        int? column,
        string? diagnosticId,
        StepResponse? seedFail)
    {
        _bus = bus;
        _plan = plan;
        _file = file;
        _line = line;
        _column = column;
        _diagnosticId = diagnosticId;
        _seedFail = seedFail;
    }

    /// <summary>Pick action whose title contains this (case-insensitive).</summary>
    public FixAt TitleContains(string titleContains)
    {
        _titleContains = titleContains;
        return this;
    }

    /// <summary>Escape: pick action_index from get_code_actions directly.</summary>
    public FixAt Index(int actionIndex)
    {
        _actionIndex = actionIndex;
        return this;
    }

    public FixAt AllDocument()
    {
        _fixAllScope = "document";
        return this;
    }

    public FixAt AllProject()
    {
        _fixAllScope = "project";
        return this;
    }

    public FixAt AllSolution()
    {
        _fixAllScope = "solution";
        return this;
    }

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        _seedFail is not null
            ? Task.FromResult(_seedFail)
            : FixRunner.RunAsync(
                _bus, _plan, _file!, _line!.Value, _column!.Value, _diagnosticId,
                _titleContains, _actionIndex, _fixAllScope, ct);
}

/// <summary>One compiler/analyzer diagnostic locus (agent-facing).</summary>
public sealed partial record IdeDiagnostic(
    string FilePath,
    int Line,
    int Column,
    string Severity,
    string Id,
    string Message)
{
    [GeneratedRegex(
        @"^(?<file>.+):(?<line>\d+):(?<column>\d+)\s+(?<severity>\w+)\s+(?<id>[A-Za-z]+\d+)\s+[—\-–]\s*(?<message>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticLine();

    public static bool TryParse(string line, out IdeDiagnostic diagnostic)
    {
        diagnostic = default!;
        if (string.IsNullOrWhiteSpace(line))
            return false;
        var m = DiagnosticLine().Match(line.Trim());
        if (!m.Success)
            return false;
        diagnostic = new IdeDiagnostic(
            m.Groups["file"].Value.Trim(),
            int.Parse(m.Groups["line"].Value),
            int.Parse(m.Groups["column"].Value),
            m.Groups["severity"].Value,
            m.Groups["id"].Value,
            m.Groups["message"].Value.Trim());
        return true;
    }
}

internal static partial class FixRunner
{
    public const string Kind = "fix.at";

    [GeneratedRegex(@"^(?<idx>\d+)\t(?<title>.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ActionLine();

    public static async Task<StepResponse> RunAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string filePath,
        int line,
        int column,
        string? diagnosticId,
        string? titleContains,
        int? forcedIndex,
        string? fixAllScope,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return StepResponse.Fail(Kind, "file path is required");
        if (line < 1 || column < 1)
            return StepResponse.Fail(Kind, "line/column must be 1-based");

        var file = Path.IsPathRooted(filePath)
            ? Path.GetFullPath(filePath)
            : Path.GetFullPath(Path.Combine(
                string.IsNullOrWhiteSpace(plan.WorkRoot) ? Directory.GetCurrentDirectory() : plan.WorkRoot,
                filePath.Replace('/', Path.DirectorySeparatorChar)));
        file = plan.Resolve(file);

        string sol;
        try
        {
            sol = plan.SolutionOrProjectPath
                  ?? throw new InvalidOperationException("solution_or_project_path required (cdp_open .sln/.csproj).");
        }
        catch (Exception ex)
        {
            return StepResponse.Fail(Kind, ex.Message);
        }

        var listRaw = await bus.InvokeAsync("roslyn", "roslyn_get_code_actions", ScriptArgs.From(new
        {
            solution_or_project_path = sol,
            file_path = file,
            line,
            column
        }), ct).ConfigureAwait(false);

        var actions = ParseActions(listRaw);
        if (actions.Count == 0)
            return StepResponse.Fail(Kind, "no code actions at locus", new { file, line, column, list = listRaw });

        int actionIndex;
        string title;
        if (forcedIndex is int fi)
        {
            var hit = actions.Where(a => a.Index == fi).Take(1).ToArray();
            if (hit.Length == 0)
                return StepResponse.Fail(Kind, $"action_index {fi} not in list", new { actions });
            actionIndex = hit[0].Index;
            title = hit[0].Title;
        }
        else if (!TryPickAction(actions, titleContains, out actionIndex, out title))
        {
            return StepResponse.Fail(Kind, "no matching code action", new
            {
                title_contains = titleContains,
                diagnostic_id = diagnosticId,
                actions = actions.Select(a => new { a.Index, a.Title })
            });
        }

        object applyArgs = string.IsNullOrWhiteSpace(fixAllScope)
            ? new
            {
                solution_or_project_path = sol,
                file_path = file,
                line,
                column,
                action_index = actionIndex
            }
            : new
            {
                solution_or_project_path = sol,
                file_path = file,
                line,
                column,
                action_index = actionIndex,
                fix_all_scope = fixAllScope
            };

        var applyRaw = await bus.InvokeAsync("roslyn", "roslyn_apply_code_action", ScriptArgs.From(applyArgs), ct)
            .ConfigureAwait(false);

        return StepResponse.Success(Kind, $"Applied: {title}", new
        {
            file,
            line,
            column,
            diagnostic_id = diagnosticId,
            action_title = title,
            action_index = actionIndex,
            fix_all_scope = fixAllScope,
            title_contains = titleContains,
            apply = NormalizeRoslynMutate(applyRaw, "roslyn.apply_code_action")
        });
    }

    private static bool TryPickAction(
        IReadOnlyList<(int Index, string Title)> actions,
        string? titleContains,
        out int index,
        out string title)
    {
        index = -1;
        title = "";

        if (!string.IsNullOrWhiteSpace(titleContains))
        {
            var hit = actions.FirstOrDefault(a => a.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase));
            if (hit.Title is { Length: > 0 })
            {
                index = hit.Index;
                title = hit.Title;
                return true;
            }

            return false;
        }

        var usingFix = actions.FirstOrDefault(a =>
            a.Title.StartsWith("using ", StringComparison.OrdinalIgnoreCase));
        if (usingFix.Title is { Length: > 0 })
        {
            index = usingFix.Index;
            title = usingFix.Title;
            return true;
        }

        foreach (var a in actions)
        {
            if (a.Title.Contains("Extract method", StringComparison.OrdinalIgnoreCase))
                continue;
            if (a.Title.StartsWith("Generate ", StringComparison.OrdinalIgnoreCase))
                continue;
            index = a.Index;
            title = a.Title;
            return true;
        }

        if (actions.Count > 0)
        {
            index = actions[0].Index;
            title = actions[0].Title;
            return true;
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

    internal static StepResponse NormalizeRoslynMutate(string raw, string kind)
    {
        var step = StepResponse.ParseOrWrap(raw, kind);
        if (step.Ok)
            return step;
        if (raw.Contains("Files updated", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("Applied:", StringComparison.OrdinalIgnoreCase))
            return StepResponse.Success(kind, "applied", new { raw });
        return step;
    }
}
