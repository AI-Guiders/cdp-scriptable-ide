namespace Cdp.ScriptableIde;

public sealed class RoslynFacade(IScriptToolBus bus)
{
    public Task<string> PingAsync(CancellationToken ct = default) =>
        bus.InvokeAsync("roslyn", "roslyn_ping", ScriptArgs.From(new { }), ct);

    public async Task<StepResponse> GetDiagnosticsAsync(string solutionOrProjectPath, string? filePath = null, CancellationToken ct = default)
    {
        var raw = await bus.InvokeAsync("roslyn", "roslyn_get_diagnostics", ScriptArgs.From(new
        {
            solution_or_project_path = solutionOrProjectPath,
            file_path = filePath
        }), ct).ConfigureAwait(false);
        return StepResponse.ParseOrWrap(raw, "roslyn.get_diagnostics");
    }

    public Task<string> RenameAsync(
        string solutionOrProjectPath,
        string filePath,
        int line,
        int column,
        string newName,
        bool apply = false,
        CancellationToken ct = default) =>
        bus.InvokeAsync("roslyn", "roslyn_rename", ScriptArgs.From(new
        {
            solution_or_project_path = solutionOrProjectPath,
            file_path = filePath,
            line,
            column,
            new_name = newName,
            apply
        }), ct);

    public Task<string> GetDocumentSymbolsAsync(string filePath, CancellationToken ct = default) =>
        bus.InvokeAsync("roslyn", "roslyn_get_document_symbols", ScriptArgs.From(new { file_path = filePath }), ct);

    public Task<string> FindUsagesAsync(
        string solutionOrProjectPath,
        string filePath,
        int line,
        int column,
        CancellationToken ct = default) =>
        bus.InvokeAsync("roslyn", "roslyn_find_usages", ScriptArgs.From(new
        {
            solution_or_project_path = solutionOrProjectPath,
            file_path = filePath,
            line,
            column
        }), ct);

    public Task<string> FormatDocumentAsync(
        string solutionOrProjectPath,
        string filePath,
        bool apply = true,
        bool aggressive = false,
        CancellationToken ct = default) =>
        bus.InvokeAsync("roslyn", "roslyn_format_document", ScriptArgs.From(new
        {
            solution_or_project_path = solutionOrProjectPath,
            file_path = filePath,
            apply,
            aggressive
        }), ct);

    /// <summary>In-proc format; returns <see cref="StepResponse"/> (<c>kind=roslyn.format</c>).</summary>
    public async Task<StepResponse> FormatAsync(
        string solutionOrProjectPath,
        string filePath,
        bool apply = true,
        bool aggressive = false,
        CancellationToken ct = default)
    {
        var raw = await FormatDocumentAsync(solutionOrProjectPath, filePath, apply, aggressive, ct).ConfigureAwait(false);
        return StepResponse.ParseOrWrap(raw, "roslyn.format");
    }

    /// <summary>Explicit Code Cleanup (<c>dotnet format</c>). Not part of Extract. Returns <see cref="StepResponse"/>.</summary>
    public async Task<StepResponse> CleanupAsync(
        string solutionOrProjectPath,
        string? filePath = null,
        bool apply = true,
        string? profile = null,
        CancellationToken ct = default)
    {
        var raw = await bus.InvokeAsync("roslyn", "roslyn_cleanup_document", ScriptArgs.From(new
        {
            solution_or_project_path = solutionOrProjectPath,
            file_path = filePath,
            apply,
            profile
        }), ct).ConfigureAwait(false);
        return StepResponse.ParseOrWrap(raw, "roslyn.cleanup");
    }

    /// <summary>Escape hatch — prefer <c>Fix.At</c> for agent brand.</summary>
    public async Task<StepResponse> GetCodeActionsAsync(
        string solutionOrProjectPath,
        string filePath,
        int line,
        int column,
        int? endLine = null,
        int? endColumn = null,
        CancellationToken ct = default)
    {
        object args = endLine is int el && endColumn is int ec
            ? new
            {
                solution_or_project_path = solutionOrProjectPath,
                file_path = filePath,
                line,
                column,
                end_line = el,
                end_column = ec
            }
            : new
            {
                solution_or_project_path = solutionOrProjectPath,
                file_path = filePath,
                line,
                column
            };
        var raw = await bus.InvokeAsync("roslyn", "roslyn_get_code_actions", ScriptArgs.From(args), ct)
            .ConfigureAwait(false);
        return StepResponse.ParseOrWrap(raw, "roslyn.get_code_actions");
    }

    /// <summary>Escape hatch — prefer <c>Fix.At</c> for agent brand.</summary>
    public async Task<StepResponse> ApplyCodeActionAsync(
        string solutionOrProjectPath,
        string filePath,
        int line,
        int column,
        int actionIndex,
        string? fixAllScope = null,
        int? endLine = null,
        int? endColumn = null,
        CancellationToken ct = default)
    {
        object args = (fixAllScope, endLine, endColumn) switch
        {
            (not null and not "", int el, int ec) => new
            {
                solution_or_project_path = solutionOrProjectPath,
                file_path = filePath,
                line,
                column,
                end_line = el,
                end_column = ec,
                action_index = actionIndex,
                fix_all_scope = fixAllScope
            },
            (not null and not "", _, _) => new
            {
                solution_or_project_path = solutionOrProjectPath,
                file_path = filePath,
                line,
                column,
                action_index = actionIndex,
                fix_all_scope = fixAllScope
            },
            (_, int el, int ec) => new
            {
                solution_or_project_path = solutionOrProjectPath,
                file_path = filePath,
                line,
                column,
                end_line = el,
                end_column = ec,
                action_index = actionIndex
            },
            _ => new
            {
                solution_or_project_path = solutionOrProjectPath,
                file_path = filePath,
                line,
                column,
                action_index = actionIndex
            }
        };
        var raw = await bus.InvokeAsync("roslyn", "roslyn_apply_code_action", ScriptArgs.From(args), ct)
            .ConfigureAwait(false);
        return FixRunner.NormalizeRoslynMutate(raw, "roslyn.apply_code_action");
    }
}
