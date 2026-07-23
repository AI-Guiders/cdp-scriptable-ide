using Microsoft.CodeAnalysis.Text;

namespace Cdp.ScriptableIde;

/// <summary>Positional insert — Before/After Anchor (MLP map; entity intents preferred when they apply).</summary>
public sealed class InsertFacade(ScriptToolBus bus, PlanContext plan)
{
    public InsertAt Before(string anchorTarget) => new(bus, plan, anchorTarget, before: true);
    public InsertAt Before(Anchor anchor) => Before(anchor.ToWire());
    public InsertAt Before(BracketLocate.Span span) => Before(BracketLocate.Format(span));

    public InsertAt After(string anchorTarget) => new(bus, plan, anchorTarget, before: false);
    public InsertAt After(Anchor anchor) => After(anchor.ToWire());
    public InsertAt After(BracketLocate.Span span) => After(BracketLocate.Format(span));
}

public sealed class InsertAt(ScriptToolBus bus, PlanContext plan, string anchorTarget, bool before)
{
    private string? _text;

    public InsertAt WithText(string text)
    {
        _text = text;
        return this;
    }

    public InsertAt Content(string text) => WithText(text);

    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        InsertRunner.ApplyAsync(bus, plan, anchorTarget, before, _text, ct);
}

internal static class InsertRunner
{
    public const string Kind = "insert.text";

    public static Task<StepResponse> ApplyAsync(
        ScriptToolBus bus,
        PlanContext plan,
        string anchorTarget,
        bool before,
        string? text,
        CancellationToken ct)
    {
        _ = ct;
        if (text is null)
            return Task.FromResult(StepResponse.Fail(Kind, "WithText/Content is required"));

        if (!TryResolveFile(plan, anchorTarget, out var file, out var span, out var fail))
            return Task.FromResult(fail!);

        if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(StepResponse.Fail(Kind, "csharp projection only (.cs)", new { file }));

        if (!TryResolveRange(file, span, out var range, out var detail))
            return Task.FromResult(StepResponse.Fail(Kind, $"locate failed: {detail}", new { bracket = anchorTarget }));

        var source = SourceText.From(File.ReadAllText(file));
        if (!TryOffset(source, range, before, out var offset, out var offsetDetail))
            return Task.FromResult(StepResponse.Fail(Kind, offsetDetail, new { range, before }));

        var newText = source.ToString(new TextSpan(0, offset))
                      + text
                      + source.ToString(new TextSpan(offset, source.Length - offset));

        var args = ScriptArgs.From(new
        {
            file,
            bracket = anchorTarget,
            before,
            offset,
            locate = detail
        });

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(Kind, "dry_run", new
            {
                dry_run = true,
                path = file,
                before,
                offset,
                locate = detail,
                preview = text.Length > 200 ? text[..200] + "…" : text
            });
            bus.RecordLocal("insert", Kind, args, dry.ToJson(), skippedDryRun: true);
            return Task.FromResult(dry);
        }

        File.WriteAllText(file, newText);
        var result = StepResponse.Success(Kind, before ? "inserted_before" : "inserted_after", new
        {
            path = file,
            before,
            offset,
            locate = detail,
            bytes = text.Length,
            work_root = plan.WorkRoot
        });
        bus.RecordLocal("insert", Kind, args, result.ToJson(), skippedDryRun: false);
        return Task.FromResult(result);
    }

    private static bool TryResolveRange(
        string file,
        BracketLocate.Span span,
        out BracketSyntaxResolve.TextRange range,
        out string detail)
    {
        if (BracketSyntaxResolve.TryResolve(file, span, out range, out detail))
            return true;

        // L-only fallback without full syntax attach (same grain as Extract L:)
        if (span.LineStart is >= 1 && span.LineEnd is >= 1)
        {
            var startCol = 1;
            var endCol = 1;
            try
            {
                var lines = File.ReadAllLines(file);
                if (span.LineStart.Value <= lines.Length)
                {
                    var lineText = lines[span.LineStart.Value - 1];
                    var trim = lineText.Length - lineText.TrimStart().Length;
                    startCol = Math.Max(1, trim + 1);
                }

                if (span.LineEnd.Value <= lines.Length)
                    endCol = Math.Max(1, lines[span.LineEnd.Value - 1].Length);
            }
            catch
            {
                // keep defaults
            }

            range = new BracketSyntaxResolve.TextRange(
                span.LineStart.Value, startCol, span.LineEnd.Value, endCol);
            detail = "line_range";
            return true;
        }

        range = default!;
        return false;
    }

    private static bool TryOffset(
        SourceText source,
        BracketSyntaxResolve.TextRange range,
        bool before,
        out int offset,
        out string detail)
    {
        offset = 0;
        detail = "";
        try
        {
            var line = before ? range.LineStart : range.LineEnd;
            var col = before ? range.ColumnStart : range.ColumnEnd;
            if (line < 1 || line > source.Lines.Count)
            {
                detail = "line_out_of_range";
                return false;
            }

            var textLine = source.Lines[line - 1];
            var maxCol = textLine.Span.Length + 1; // 1-based exclusive end may be Length+1
            var col0 = Math.Clamp(col - 1, 0, textLine.Span.Length);
            offset = textLine.Start + col0;
            _ = maxCol;
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    private static bool TryResolveFile(
        PlanContext plan,
        string anchorTarget,
        out string file,
        out BracketLocate.Span span,
        out StepResponse? fail)
    {
        file = "";
        span = BracketLocate.Parse(anchorTarget);
        fail = null;
        if (string.IsNullOrWhiteSpace(span.File))
        {
            fail = StepResponse.Fail(Kind, "bracket must include F:path");
            return false;
        }

        file = span.File!;
        if (!Path.IsPathRooted(file))
        {
            var root = plan.WorkRoot;
            if (string.IsNullOrWhiteSpace(root))
            {
                fail = StepResponse.Fail(Kind, "relative F: needs Plan.WorkRoot (cdp_open) or absolute F:");
                return false;
            }

            file = Path.GetFullPath(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
        }
        else
        {
            file = Path.GetFullPath(file);
        }

        file = plan.Resolve(file);
        return true;
    }
}
