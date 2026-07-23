using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynTextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace Cdp.ScriptableIde;

/// <summary>Binary fold ops for <see cref="CodeMoveTo.Operation"/> into an expression locus (e.g. K:Condition).</summary>
public enum CodeOp
{
    And,
    Or
}

/// <summary>Statement/span relocate — and expression fold (And/Or) into Condition-class anchors.</summary>
public sealed class CodeFacade(IScriptToolBus bus, PlanContext plan)
{
    public CodeMoveBuilder Move() => new(bus, plan);
}

public sealed class CodeMoveBuilder(IScriptToolBus bus, PlanContext plan)
{
    public CodeMoveFrom From(string anchorTarget) => new(bus, plan, anchorTarget);
    public CodeMoveFrom From(Anchor anchor) => From(anchor.ToWire());
    public CodeMoveFrom From(BracketLocate.Span span) => From(BracketLocate.Format(span));
}

public sealed class CodeMoveFrom(IScriptToolBus bus, PlanContext plan, string fromTarget)
{
    /// <summary>Destination locus — then <see cref="CodeMoveTo.Before"/> / <see cref="CodeMoveTo.After"/> / <see cref="CodeMoveTo.Operation"/>.</summary>
    public CodeMoveTo To(string destTarget) => new(bus, plan, fromTarget, destTarget);
    public CodeMoveTo To(Anchor dest) => To(dest.ToWire());
    public CodeMoveTo To(BracketLocate.Span dest) => To(BracketLocate.Format(dest));

    /// <summary>Shortcut: move From before dest (same as <c>To(dest).Before()</c>).</summary>
    public CodeMoveApply Before(string destTarget) => To(destTarget).Before();
    public CodeMoveApply Before(Anchor dest) => Before(dest.ToWire());

    /// <summary>Shortcut: move From after dest.</summary>
    public CodeMoveApply After(string destTarget) => To(destTarget).After();
    public CodeMoveApply After(Anchor dest) => After(dest.ToWire());

    /// <summary>Shortcut: fold From into dest expression with <c>&amp;&amp;</c>.</summary>
    public CodeMoveApply And(string destTarget) => To(destTarget).And();
    public CodeMoveApply And(Anchor dest) => And(dest.ToWire());

    /// <summary>Shortcut: fold From into dest expression with <c>||</c>.</summary>
    public CodeMoveApply Or(string destTarget) => To(destTarget).Or();
    public CodeMoveApply Or(Anchor dest) => Or(dest.ToWire());
}

public sealed class CodeMoveTo(IScriptToolBus bus, PlanContext plan, string fromTarget, string toTarget)
{
    public CodeMoveApply Before() => new(bus, plan, fromTarget, toTarget, before: true, combineOp: null);
    public CodeMoveApply After() => new(bus, plan, fromTarget, toTarget, before: false, combineOp: null);

    /// <summary>Fold From into To expression: <c>to &amp;&amp; from</c> (cut From). Dest = expression locus (K:Condition).</summary>
    public CodeMoveApply And() => Operation(CodeOp.And);

    /// <summary>Fold From into To expression: <c>to || from</c> (cut From).</summary>
    public CodeMoveApply Or() => Operation(CodeOp.Or);

    /// <summary>Fold From into To with binary op — e.g. <c>Operation(CodeOp.And)</c>.</summary>
    public CodeMoveApply Operation(CodeOp op) => new(bus, plan, fromTarget, toTarget, before: null, combineOp: op);
}

public sealed class CodeMoveApply(
    IScriptToolBus bus,
    PlanContext plan,
    string fromTarget,
    string toTarget,
    bool? before,
    CodeOp? combineOp)
{
    public Task<StepResponse> ApplyAsync(CancellationToken ct = default) =>
        CodeMoveRunner.ApplyAsync(bus, plan, fromTarget, toTarget, before, combineOp, ct);
}

internal static class CodeMoveRunner
{
    public const string Kind = "code.move";

    public static Task<StepResponse> ApplyAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string fromTarget,
        string toTarget,
        bool? before,
        CodeOp? combineOp,
        CancellationToken ct)
    {
        _ = ct;
        if (combineOp is null && before is null)
            return Task.FromResult(StepResponse.Fail(Kind, "need Before/After or Operation"));
        if (combineOp is not null && before is not null)
            return Task.FromResult(StepResponse.Fail(Kind, "Before/After and Operation are mutually exclusive"));

        if (!TryResolveFile(plan, fromTarget, out var fromFile, out var fromSpan, out var fail))
            return Task.FromResult(fail!);
        if (!TryResolveFile(plan, toTarget, out var toFile, out var toSpan, out fail))
            return Task.FromResult(fail!);

        if (!fromFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || !toFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(StepResponse.Fail(Kind, "csharp-only (.cs) v1", new { fromFile, toFile }));

        if (!TryResolveRange(fromFile, fromSpan, out var fromRange, out var fromDetail, out var fromLineGrain))
            return Task.FromResult(StepResponse.Fail(Kind, $"From locate failed: {fromDetail}", new { from = fromTarget }));
        if (!TryResolveRange(toFile, toSpan, out var toRange, out var toDetail, out var toLineGrain))
            return Task.FromResult(StepResponse.Fail(Kind, $"To locate failed: {toDetail}", new { to = toTarget }));

        var sameFile = string.Equals(fromFile, toFile, StringComparison.OrdinalIgnoreCase);
        var args = ScriptArgs.From(new
        {
            from = fromTarget,
            to = toTarget,
            before,
            op = combineOp?.ToString(),
            from_file = fromFile,
            to_file = toFile,
            from_locate = fromDetail,
            to_locate = toDetail,
            from_line_grain = fromLineGrain,
            to_line_grain = toLineGrain
        });

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(Kind, "dry_run", new
            {
                dry_run = true,
                before,
                op = combineOp?.ToString(),
                same_file = sameFile,
                from_file = fromFile,
                to_file = toFile
            });
            bus.RecordLocal("code", Kind, args, dry.ToJson(), skippedDryRun: true);
            return Task.FromResult(dry);
        }

        try
        {
            if (combineOp is { } op)
            {
                if (toLineGrain)
                    throw new InvalidOperationException(
                        "Operation fold needs expression locus (K:Condition|Initializer|…), not bare L-only line");
                if (sameFile)
                    CombineSameFile(fromFile, fromRange, fromLineGrain, toRange, op);
                else
                    CombineCrossFile(fromFile, fromRange, fromLineGrain, toFile, toRange, op);
            }
            else if (sameFile)
                MoveSameFile(fromFile, fromRange, fromLineGrain, toRange, toLineGrain, before!.Value);
            else
                MoveCrossFile(fromFile, fromRange, fromLineGrain, toFile, toRange, toLineGrain, before!.Value);
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(StepResponse.Fail(Kind, ex.Message, new { from = fromTarget, to = toTarget }));
        }

        var summary = combineOp switch
        {
            CodeOp.And => "folded_and",
            CodeOp.Or => "folded_or",
            _ => before!.Value ? "moved_before" : "moved_after"
        };
        var ok = StepResponse.Success(Kind, summary, new
        {
            from_file = fromFile,
            to_file = toFile,
            before,
            op = combineOp?.ToString(),
            same_file = sameFile,
            work_root = plan.WorkRoot
        });
        bus.RecordLocal("code", Kind, args, ok.ToJson());
        return Task.FromResult(ok);
    }

    private static void CombineSameFile(
        string file,
        BracketSyntaxResolve.TextRange fromRange,
        bool fromLineGrain,
        BracketSyntaxResolve.TextRange toRange,
        CodeOp op)
    {
        var text = File.ReadAllText(file);
        var source = SourceText.From(text);
        if (!TryExtractExpression(text, source, fromRange, fromLineGrain, out var exprText, out var fromStart, out var fromEnd))
            throw new InvalidOperationException("From expression extract failed");
        if (!TryFromOffsets(source, toRange, lineGrain: false, out var toStart, out var toEnd))
            throw new InvalidOperationException("To expression offsets failed");

        if (RangesOverlap(fromStart, fromEnd, toStart, toEnd))
            throw new InvalidOperationException("From/To overlap — combine refused");

        var leftText = text[toStart..toEnd];
        var combined = FoldExpressions(leftText, exprText, op);

        // Delete From first, then replace To (adjust if To was after From).
        var afterDelete = text[..fromStart] + text[fromEnd..];
        var adjToStart = toStart;
        var adjToEnd = toEnd;
        if (toStart > fromStart)
        {
            var delta = fromEnd - fromStart;
            adjToStart -= delta;
            adjToEnd -= delta;
        }

        if (adjToStart < 0 || adjToEnd > afterDelete.Length || adjToStart > adjToEnd)
            throw new InvalidOperationException("adjusted To out of range");

        var moved = afterDelete[..adjToStart] + combined + afterDelete[adjToEnd..];
        File.WriteAllText(file, moved);
    }

    private static void CombineCrossFile(
        string fromFile,
        BracketSyntaxResolve.TextRange fromRange,
        bool fromLineGrain,
        string toFile,
        BracketSyntaxResolve.TextRange toRange,
        CodeOp op)
    {
        var fromText = File.ReadAllText(fromFile);
        var fromSource = SourceText.From(fromText);
        if (!TryExtractExpression(fromText, fromSource, fromRange, fromLineGrain, out var exprText, out var fromStart, out var fromEnd))
            throw new InvalidOperationException("From expression extract failed");

        var toText = File.ReadAllText(toFile);
        var toSource = SourceText.From(toText);
        if (!TryFromOffsets(toSource, toRange, lineGrain: false, out var toStart, out var toEnd))
            throw new InvalidOperationException("To expression offsets failed");

        var leftText = toText[toStart..toEnd];
        var combined = FoldExpressions(leftText, exprText, op);

        File.WriteAllText(fromFile, fromText[..fromStart] + fromText[fromEnd..]);
        File.WriteAllText(toFile, toText[..toStart] + combined + toText[toEnd..]);
    }

    private static string FoldExpressions(string leftText, string rightText, CodeOp op)
    {
        var left = SyntaxFactory.ParseExpression(leftText.Trim());
        var right = SyntaxFactory.ParseExpression(rightText.Trim());
        if (left.ContainsDiagnostics || right.ContainsDiagnostics)
            throw new InvalidOperationException("From/To did not parse as expressions");

        var (kind, tokenKind) = op switch
        {
            CodeOp.And => (SyntaxKind.LogicalAndExpression, SyntaxKind.AmpersandAmpersandToken),
            CodeOp.Or => (SyntaxKind.LogicalOrExpression, SyntaxKind.BarBarToken),
            _ => throw new InvalidOperationException($"unsupported op {op}")
        };
        var opToken = SyntaxFactory.Token(tokenKind)
            .WithLeadingTrivia(SyntaxFactory.Space)
            .WithTrailingTrivia(SyntaxFactory.Space);
        var bin = SyntaxFactory.BinaryExpression(kind, left, opToken, right);
        return bin.ToFullString();
    }

    private static bool TryExtractExpression(
        string text,
        SourceText source,
        BracketSyntaxResolve.TextRange range,
        bool lineGrain,
        out string exprText,
        out int deleteStart,
        out int deleteEnd)
    {
        exprText = "";
        deleteStart = 0;
        deleteEnd = 0;
        if (!TryFromOffsets(source, range, lineGrain, out deleteStart, out deleteEnd))
            return false;

        var tree = CSharpSyntaxTree.ParseText(text);
        var root = tree.GetCompilationUnitRoot();
        var findEnd = deleteEnd;
        while (findEnd > deleteStart && char.IsWhiteSpace(text[findEnd - 1]))
            findEnd--;
        if (findEnd <= deleteStart)
            return false;

        var findSpan = RoslynTextSpan.FromBounds(deleteStart, findEnd);
        var node = root.FindNode(findSpan, findInsideTrivia: false, getInnermostNodeForTie: true);

        if (node.AncestorsAndSelf().OfType<ExpressionStatementSyntax>().FirstOrDefault() is { } stmt)
        {
            exprText = stmt.Expression.ToString().Trim();
            // Prefer whole-line delete when the statement owns the line (dogfood: drop `d.IsSmth();`).
            var line = source.Lines.GetLineFromPosition(stmt.SpanStart);
            var lineText = source.ToString(line.Span).Trim();
            var stmtText = stmt.ToString().Trim();
            if (lineText == stmtText || lineText == stmtText + ";")
            {
                deleteStart = line.Start;
                deleteEnd = line.Start + line.SpanIncludingLineBreak.Length;
            }
            else
            {
                deleteStart = stmt.FullSpan.Start;
                deleteEnd = stmt.FullSpan.End;
            }

            return exprText.Length > 0;
        }

        if (node.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault() is { } expr)
        {
            // Prefer the widest expression still inside the delete window.
            ExpressionSyntax best = expr;
            for (var cur = expr; cur != null; cur = cur.Parent as ExpressionSyntax)
            {
                if (cur.Span.Start >= deleteStart && cur.Span.End <= findEnd)
                    best = cur;
            }

            exprText = best.ToString().Trim();
            deleteStart = best.SpanStart;
            deleteEnd = best.Span.End;
            return exprText.Length > 0;
        }

        var raw = text[deleteStart..deleteEnd].Trim();
        if (raw.EndsWith(';'))
            raw = raw[..^1].Trim();
        exprText = raw;
        return exprText.Length > 0;
    }

    private static bool RangesOverlap(int a0, int a1, int b0, int b1) =>
        a0 < b1 && b0 < a1;

    private static void MoveSameFile(
        string file,
        BracketSyntaxResolve.TextRange fromRange,
        bool fromLineGrain,
        BracketSyntaxResolve.TextRange toRange,
        bool toLineGrain,
        bool before)
    {
        var source = SourceText.From(File.ReadAllText(file));
        if (!TryFromOffsets(source, fromRange, fromLineGrain, out var fromStart, out var fromEnd))
            throw new InvalidOperationException("From offsets failed");
        if (!TryToInsertOffset(source, toRange, toLineGrain, before, out var toInsert))
            throw new InvalidOperationException("To insert offset failed");

        if (toInsert > fromStart && toInsert < fromEnd)
            throw new InvalidOperationException("To locus is inside From span — overlapping move refused");

        var chunk = source.ToString(RoslynTextSpan.FromBounds(fromStart, fromEnd));
        if (!fromLineGrain)
            chunk = EnsureTrailingNewline(chunk);

        // Delete first, then adjust insert point if it was after the deleted span.
        var afterDelete = source.ToString(new RoslynTextSpan(0, fromStart))
                          + source.ToString(new RoslynTextSpan(fromEnd, source.Length - fromEnd));
        var insertAt = toInsert;
        if (toInsert > fromStart)
            insertAt -= fromEnd - fromStart;
        if (insertAt < 0 || insertAt > afterDelete.Length)
            throw new InvalidOperationException("adjusted insert out of range");

        var moved = afterDelete[..insertAt] + chunk + afterDelete[insertAt..];
        File.WriteAllText(file, moved);
    }

    private static void MoveCrossFile(
        string fromFile,
        BracketSyntaxResolve.TextRange fromRange,
        bool fromLineGrain,
        string toFile,
        BracketSyntaxResolve.TextRange toRange,
        bool toLineGrain,
        bool before)
    {
        var fromSource = SourceText.From(File.ReadAllText(fromFile));
        if (!TryFromOffsets(fromSource, fromRange, fromLineGrain, out var fromStart, out var fromEnd))
            throw new InvalidOperationException("From offsets failed");

        var chunk = fromSource.ToString(RoslynTextSpan.FromBounds(fromStart, fromEnd));
        if (!fromLineGrain)
            chunk = EnsureTrailingNewline(chunk);
        var fromNew = fromSource.ToString(new RoslynTextSpan(0, fromStart))
                      + fromSource.ToString(new RoslynTextSpan(fromEnd, fromSource.Length - fromEnd));
        File.WriteAllText(fromFile, fromNew);

        var toSource = SourceText.From(File.ReadAllText(toFile));
        if (!TryToInsertOffset(toSource, toRange, toLineGrain, before, out var toInsert))
            throw new InvalidOperationException("To insert offset failed");
        var toNew = toSource.ToString(new RoslynTextSpan(0, toInsert))
                    + chunk
                    + toSource.ToString(new RoslynTextSpan(toInsert, toSource.Length - toInsert));
        File.WriteAllText(toFile, toNew);
    }

    private static string EnsureTrailingNewline(string chunk)
    {
        if (chunk.EndsWith('\n'))
            return chunk;
        return chunk + "\n";
    }

    /// <summary>
    /// L-only anchors = whole lines (incl. line break). Syntax/M/S = node span.
    /// Dogfood: L: must not collapse to innermost token (e.g. just <c>var</c>).
    /// </summary>
    private static bool IsLineOnlySpan(BracketLocate.Span span) =>
        span.LineStart is >= 1
        && span.LineEnd is >= 1
        && string.IsNullOrWhiteSpace(span.MemberKey)
        && string.IsNullOrWhiteSpace(span.ScopeKind)
        && string.IsNullOrWhiteSpace(span.Role);

    private static bool TryFromOffsets(
        SourceText source,
        BracketSyntaxResolve.TextRange range,
        bool lineGrain,
        out int start,
        out int end)
    {
        if (lineGrain)
            return TryWholeLineSpan(source, range.LineStart, range.LineEnd, out start, out end);

        start = 0;
        end = 0;
        if (!TryNodeOffset(source, range, before: true, out start))
            return false;
        if (!TryNodeOffset(source, range, before: false, out end))
            return false;
        if (end < start)
            (start, end) = (end, start);
        return end > start;
    }

    private static bool TryToInsertOffset(
        SourceText source,
        BracketSyntaxResolve.TextRange range,
        bool lineGrain,
        bool before,
        out int offset)
    {
        if (lineGrain)
            return TryLineBoundary(source, before ? range.LineStart : range.LineEnd, before, out offset);
        return TryNodeOffset(source, range, before, out offset);
    }

    private static bool TryWholeLineSpan(SourceText source, int lineStart1, int lineEnd1, out int start, out int end)
    {
        start = 0;
        end = 0;
        if (lineStart1 < 1 || lineEnd1 < lineStart1 || lineEnd1 > source.Lines.Count)
            return false;
        start = source.Lines[lineStart1 - 1].Start;
        // End = start of line after lineEnd (includes trailing break of last moved line).
        end = lineEnd1 < source.Lines.Count
            ? source.Lines[lineEnd1].Start
            : source.Length;
        return end > start;
    }

    private static bool TryLineBoundary(SourceText source, int line1, bool before, out int offset)
    {
        offset = 0;
        if (line1 < 1 || line1 > source.Lines.Count)
            return false;
        if (before)
        {
            offset = source.Lines[line1 - 1].Start;
            return true;
        }

        // After line → start of next line / EOF (past its line break).
        offset = line1 < source.Lines.Count ? source.Lines[line1].Start : source.Length;
        return true;
    }

    private static bool TryNodeOffset(
        SourceText source,
        BracketSyntaxResolve.TextRange range,
        bool before,
        out int offset)
    {
        offset = 0;
        try
        {
            var line = before ? range.LineStart : range.LineEnd;
            var col = before ? range.ColumnStart : range.ColumnEnd;
            if (line < 1 || line > source.Lines.Count)
                return false;
            var textLine = source.Lines[line - 1];
            var col0 = Math.Clamp(col - 1, 0, textLine.Span.Length);
            offset = textLine.Start + col0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveRange(
        string file,
        BracketLocate.Span span,
        out BracketSyntaxResolve.TextRange range,
        out string detail,
        out bool lineGrain)
    {
        lineGrain = false;
        // Prefer whole-line grain for L-only — syntax FindNodeAtLine is too narrow for Move.
        if (IsLineOnlySpan(span))
        {
            range = new BracketSyntaxResolve.TextRange(
                span.LineStart!.Value, 1, span.LineEnd!.Value, 1);
            detail = "line_range";
            lineGrain = true;
            return true;
        }

        if (BracketSyntaxResolve.TryResolve(file, span, out range, out detail))
            return true;

        range = default!;
        return false;
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
            fail = StepResponse.Fail(Kind, "anchor must include F:path");
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
