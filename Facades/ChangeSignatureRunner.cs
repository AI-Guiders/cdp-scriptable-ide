using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cdp.ScriptableIde;

/// <summary>
/// Change signature via local C# rewrite (declaration + same-file call sites).
/// Prefer Roslyn Change Signature when action_options mapping is reliable; this path is fail-loud and smokeable.
/// </summary>
internal static class ChangeSignatureRunner
{
    public const string Kind = "refactor.change_signature";

    public static async Task<StepResponse> RunAsync(
        IScriptToolBus bus,
        PlanContext plan,
        string anchorTarget,
        IReadOnlyList<ChangeSignatureOp> ops,
        CancellationToken ct)
    {
        if (ops.Count == 0)
            return StepResponse.Fail(Kind, "at least one Add/Remove/Move required");

        if (!AnchorLocus.TryResolveFile(plan, anchorTarget, Kind, out var file, out var span, out var fail))
            return fail!;

        if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return StepResponse.Fail(Kind, "csharp only (.cs)", new { file });

        if (!BracketSyntaxResolve.TryFindAttachTarget(file, span, out var target, out var detail))
            return StepResponse.Fail(Kind, $"locate failed: {detail}", new { anchor = anchorTarget });

        var method = FindMethod(target.Node);
        if (method is null)
            return StepResponse.Fail(Kind, "anchor must resolve to a method (M:Method)", new
            {
                node = target.Node.Kind().ToString(),
                locate = detail
            });

        var lang = string.IsNullOrWhiteSpace(plan.Language) ? "csharp" : plan.Language!;
        if (!TryBuildNewParameterList(method, ops, lang, out var newParams, out var reorder, out var err))
            return StepResponse.Fail(Kind, err!);

        var updatedMethod = method.WithParameterList(
            method.ParameterList.WithParameters(SyntaxFactory.SeparatedList(newParams)));

        var root = target.Root.ReplaceNode(method, updatedMethod);
        root = UpdateCallSites(root, method.Identifier.Text, reorder);

        if (bus.IsDryRun)
        {
            var dry = StepResponse.Success(Kind, "dry_run", new
            {
                dry_run = true,
                path = file,
                method = method.Identifier.Text,
                ops = DescribeOps(ops),
                preview = updatedMethod.ParameterList.ToFullString()
            });
            bus.RecordLocal("refactor", Kind, ScriptArgs.From(new { anchor = anchorTarget, file }), dry.ToJson(),
                skippedDryRun: true);
            return dry;
        }

        File.WriteAllText(file, root.GetText().ToString());

        StepResponse? formatStep = null;
        var sol = plan.SolutionOrProjectPath;
        if (!string.IsNullOrWhiteSpace(sol))
        {
            var formatRaw = await bus.InvokeAsync("roslyn", "roslyn_format_document", ScriptArgs.From(new
            {
                solution_or_project_path = sol,
                file_path = file,
                apply = true,
                aggressive = true
            }), ct).ConfigureAwait(false);
            formatStep = StepResponse.ParseOrWrap(formatRaw, "roslyn.format");
        }

        // Optional: also poke Change Signature code action with action_options (best-effort; local rewrite is SSOT).
        object? actionOptions = new
        {
            operations = DescribeOps(ops),
            method = method.Identifier.Text
        };
        _ = actionOptions;

        return StepResponse.Success(Kind, $"Changed signature of {method.Identifier.Text}", new
        {
            method = method.Identifier.Text,
            file,
            ops = DescribeOps(ops),
            format = formatStep
        });
    }

    private static MethodDeclarationSyntax? FindMethod(SyntaxNode node) =>
        node as MethodDeclarationSyntax
        ?? node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();

    private static object[] DescribeOps(IReadOnlyList<ChangeSignatureOp> ops) =>
        ops.Select(op => op switch
        {
            ChangeSignatureOp.Add a => (object)new
            {
                kind = "add",
                a.Name,
                direction = a.Direction.ToString(),
                @default = a.DefaultValue
            },
            ChangeSignatureOp.Remove r => new { kind = "remove", r.Name },
            ChangeSignatureOp.Move m => new
            {
                kind = "move",
                m.Name,
                how = m.Kind.ToString(),
                relative = m.RelativeTo,
                to = m.ToIndex
            },
            _ => new { kind = "unknown" }
        }).ToArray();

    /// <summary>
    /// Builds new parameter list and a reorder map: newIndex → oldIndex (-1 = new arg / use default).
    /// </summary>
    private static bool TryBuildNewParameterList(
        MethodDeclarationSyntax method,
        IReadOnlyList<ChangeSignatureOp> ops,
        string language,
        out List<ParameterSyntax> newParams,
        out List<int> reorder,
        out string? error)
    {
        newParams = [];
        reorder = [];
        error = null;

        var working = method.ParameterList.Parameters.ToList();
        var oldNames = working.Select(p => p.Identifier.Text).ToList();

        foreach (var op in ops)
        {
            switch (op)
            {
                case ChangeSignatureOp.Remove rem:
                {
                    var idx = working.FindIndex(p => p.Identifier.Text.Equals(rem.Name, StringComparison.Ordinal));
                    if (idx < 0)
                    {
                        error = $"parameter_not_found:{rem.Name}";
                        return false;
                    }

                    working.RemoveAt(idx);
                    break;
                }
                case ChangeSignatureOp.Add add:
                {
                    if (!TypeProjection.TryProject(language, add.Type, out var typeWire, out var terr))
                    {
                        error = terr ?? "type_project_failed";
                        return false;
                    }

                    if (working.Any(p => p.Identifier.Text.Equals(add.Name, StringComparison.Ordinal)))
                    {
                        error = $"parameter_exists:{add.Name}";
                        return false;
                    }

                    var p = SyntaxFactory.Parameter(SyntaxFactory.Identifier(add.Name))
                        .WithType(SyntaxFactory.ParseTypeName(typeWire).WithTrailingTrivia(SyntaxFactory.Space));

                    p = add.Direction switch
                    {
                        ParamDirection.Ref => p.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword)
                            .WithTrailingTrivia(SyntaxFactory.Space))),
                        ParamDirection.Out => p.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.OutKeyword)
                            .WithTrailingTrivia(SyntaxFactory.Space))),
                        ParamDirection.InKeyword => p.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InKeyword)
                            .WithTrailingTrivia(SyntaxFactory.Space))),
                        _ => p
                    };

                    if (!string.IsNullOrWhiteSpace(add.DefaultValue))
                    {
                        p = p.WithDefault(SyntaxFactory.EqualsValueClause(
                            SyntaxFactory.ParseExpression(add.DefaultValue!)));
                    }

                    working.Add(p);
                    break;
                }
                case ChangeSignatureOp.Move move:
                {
                    var from = working.FindIndex(p => p.Identifier.Text.Equals(move.Name, StringComparison.Ordinal));
                    if (from < 0)
                    {
                        error = $"parameter_not_found:{move.Name}";
                        return false;
                    }

                    var item = working[from];
                    working.RemoveAt(from);
                    int to;
                    if (move.Kind == ChangeSignatureOp.MoveKind.ToPosition)
                    {
                        to = move.ToIndex!.Value;
                    }
                    else if (move.Kind == ChangeSignatureOp.MoveKind.Before)
                    {
                        to = working.FindIndex(p =>
                            p.Identifier.Text.Equals(move.RelativeTo, StringComparison.Ordinal));
                    }
                    else if (move.Kind == ChangeSignatureOp.MoveKind.After)
                    {
                        var rel = working.FindIndex(p =>
                            p.Identifier.Text.Equals(move.RelativeTo, StringComparison.Ordinal));
                        to = rel < 0 ? -1 : rel + 1;
                    }
                    else
                    {
                        to = -1;
                    }

                    if (to < 0 || to > working.Count)
                    {
                        error = move.Kind == ChangeSignatureOp.MoveKind.ToPosition
                            ? $"bad_position:{move.ToIndex}"
                            : $"relative_not_found:{move.RelativeTo}";
                        return false;
                    }

                    working.Insert(to, item);
                    break;
                }
            }
        }

        newParams = working;
        // Map each new slot to old index by name (adds → -1)
        foreach (var p in working)
        {
            var oldIdx = oldNames.FindIndex(n => n.Equals(p.Identifier.Text, StringComparison.Ordinal));
            reorder.Add(oldIdx);
        }

        return true;
    }

    private static CompilationUnitSyntax UpdateCallSites(
        CompilationUnitSyntax root,
        string methodName,
        IReadOnlyList<int> reorder)
    {
        var invocations = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(inv => IsNamedCall(inv, methodName))
            .ToList();

        if (invocations.Count == 0)
            return root;

        return root.ReplaceNodes(invocations, (_, inv) =>
        {
            var args = inv.ArgumentList.Arguments;
            var newArgs = new List<ArgumentSyntax>();
            for (var i = 0; i < reorder.Count; i++)
            {
                var oldIdx = reorder[i];
                if (oldIdx >= 0 && oldIdx < args.Count)
                {
                    newArgs.Add(args[oldIdx]);
                    continue;
                }

                // New parameter: omit trailing defaults; otherwise insert `default`.
                var isTrailingNew = reorder.Skip(i).All(x => x < 0);
                if (isTrailingNew)
                    break;
                newArgs.Add(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                    SyntaxKind.DefaultLiteralExpression,
                    SyntaxFactory.Token(SyntaxKind.DefaultKeyword))));
            }

            return inv.WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs)));
        });
    }

    private static bool IsNamedCall(InvocationExpressionSyntax inv, string methodName) =>
        inv.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text.Equals(methodName, StringComparison.Ordinal),
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text.Equals(methodName, StringComparison.Ordinal),
            _ => false
        };
}
