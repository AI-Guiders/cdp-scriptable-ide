namespace Cdp.ScriptableIde;

/// <summary>Intent → test body lines (arrange / act / assert). Language wire stays in projection.</summary>
internal static class TestBodyProjection
{
    public static bool TryBuildBody(
        string language,
        PlanContext plan,
        string sutType,
        IReadOnlyList<ArrangeIntent> arranges,
        IReadOnlyList<ActIntent> acts,
        IReadOnlyList<AssertionIntent> assertions,
        TestFrameworkKind framework,
        string indent,
        string emptyHint,
        out string body,
        out string? error)
    {
        body = "";
        error = null;
        var lines = new List<string>();
        foreach (var a in arranges)
        {
            if (!TryProjectArrange(language, sutType, a, out var line, out error))
                return false;
            lines.Add(indent + line);
        }

        foreach (var act in acts)
        {
            if (!TryProjectAct(language, plan, act, out var line, out error))
                return false;
            lines.Add(indent + line);
        }

        foreach (var assertion in assertions)
            lines.Add(indent + ProjectAssertion(framework, assertion));

        if (lines.Count == 0)
            lines.Add(indent + emptyHint);
        body = string.Join("\n", lines);
        return true;
    }

    public static bool IsValidIdentifier(string name) =>
        name.Length > 0
        && (char.IsLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static bool TryProjectArrange(
        string language,
        string sutType,
        ArrangeIntent intent,
        out string line,
        out string? error)
    {
        line = "";
        error = null;
        switch (intent)
        {
            case SutArrange s:
                if (string.IsNullOrWhiteSpace(sutType))
                {
                    error = "Arrange.Sut requires a resolved SUT type from bracket";
                    return false;
                }

                if (!IsValidIdentifier(s.Local))
                {
                    error = "Arrange.Sut local must be an identifier";
                    return false;
                }

                line = ProjectNew(language, s.Local, sutType, s.ArgsWire);
                return true;
            case NewArrange n:
                if (!IsValidIdentifier(n.Local))
                {
                    error = "Arrange.New local must be an identifier";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(n.TypeName))
                {
                    error = "Arrange.New typeName is required";
                    return false;
                }

                line = ProjectNew(language, n.Local, n.TypeName.Trim(), n.ArgsWire);
                return true;
            case TypedNewArrange tn:
                if (!IsValidIdentifier(tn.Local))
                {
                    error = "Arrange.New local must be an identifier";
                    return false;
                }

                if (!TypeProjection.TryProject(language, tn.Type, out var newTypeWire, out error))
                    return false;
                line = ProjectNew(language, tn.Local, newTypeWire, tn.ArgsWire);
                return true;
            case DeclareArrange d:
                return DeclareProjection.TryProject(language, d, out line, out error);
            case StmtArrange st:
                line = NormalizeStmt(language, st.Code);
                return true;
            default:
                error = "unsupported arrange " + intent.Kind;
                return false;
        }
    }

    private static bool TryProjectAct(string language, PlanContext plan, ActIntent intent, out string line, out string? error)
    {
        line = "";
        error = null;
        switch (intent)
        {
            case CallAct c:
            {
                var receiver = c.Receiver;
                var method = c.Method;
                if (!string.IsNullOrWhiteSpace(c.MethodAnchor))
                {
                    if (!CallAnchorResolve.TryResolve(plan, c.MethodAnchor!, out var typeName, out var methodName, out error))
                        return false;
                    method = methodName;
                    // On(instance) wins; else static Type.Method
                    receiver = !string.IsNullOrWhiteSpace(c.Receiver) ? c.Receiver : typeName;
                }

                if (string.IsNullOrWhiteSpace(receiver) || string.IsNullOrWhiteSpace(method)
                    || !IsValidIdentifier(receiver!) || !IsValidIdentifier(method!))
                {
                    error = "Act.Call needs receiver+method identifiers (or Call(Anchor))";
                    return false;
                }

                if (c.Bind is { Length: > 0 } bind && !IsValidIdentifier(bind))
                {
                    error = "Act.Call bind must be an identifier";
                    return false;
                }

                foreach (var a in c.Args)
                {
                    if (a.Name is { Length: > 0 } n && !IsValidIdentifier(n))
                    {
                        error = "named CallArg name must be an identifier";
                        return false;
                    }
                }

                var args = ProjectCallArgs(language, c.Args);
                var call = $"{receiver}.{method}({args})";
                if (c.Bind is { Length: > 0 } b)
                {
                    line = language switch
                    {
                        "python" => $"{b} = {call}",
                        "typescript" => $"const {b} = {call};",
                        _ => $"var {b} = {call};"
                    };
                }
                else
                {
                    line = language == "python" ? call : call + ";";
                }

                return true;
            }
            case StmtAct st:
                line = NormalizeStmt(language, st.Code);
                return true;
            default:
                error = "unsupported act " + intent.Kind;
                return false;
        }
    }

    /// <summary>Param types are not in intent — only name/expr; named form is lang projection.</summary>
    private static string ProjectCallArgs(string language, IReadOnlyList<CallArg> args)
    {
        if (args.Count == 0)
            return "";
        return string.Join(", ", args.Select(a =>
        {
            if (string.IsNullOrWhiteSpace(a.Name))
                return a.ExprWire;
            return language switch
            {
                "python" => $"{a.Name}={a.ExprWire}",
                _ => $"{a.Name}: {a.ExprWire}"
            };
        }));
    }

    private static string ProjectNew(string language, string local, string typeName, string? argsWire)
    {
        var args = string.IsNullOrWhiteSpace(argsWire) ? "" : argsWire.Trim();
        return language switch
        {
            "python" => $"{local} = {typeName}({args})",
            "typescript" => $"const {local} = new {typeName}({args});",
            _ => $"var {local} = new {typeName}({args});"
        };
    }

    private static string NormalizeStmt(string language, string code)
    {
        var t = (code ?? "").Trim();
        if (t.Length == 0)
            return language == "python" ? "pass" : ";";
        if (language == "python")
            return t;
        return t.EndsWith(';') ? t : t + ";";
    }

    internal static string ProjectAssertion(TestFrameworkKind framework, AssertionIntent a) =>
        (framework, a) switch
        {
            (TestFrameworkKind.Xunit, EqualAssertion e) => $"Assert.Equal({e.Expected}, {e.Actual});",
            (TestFrameworkKind.Xunit, TrueAssertion t) => $"Assert.True({t.Expression});",
            (TestFrameworkKind.Xunit, FalseAssertion f) => $"Assert.False({f.Expression});",
            (TestFrameworkKind.Xunit, NullAssertion n) => $"Assert.Null({n.Expression});",
            (TestFrameworkKind.Xunit, NotNullAssertion n) => $"Assert.NotNull({n.Expression});",
            (TestFrameworkKind.Xunit, SameAssertion s) => $"Assert.Same({s.Expected}, {s.Actual});",
            (TestFrameworkKind.Xunit, ThrowsAssertion t) =>
                $"Assert.Throws<{t.ExceptionType}>({t.ActionExpression});",
            (TestFrameworkKind.NUnit, EqualAssertion e) => $"Assert.That({e.Actual}, Is.EqualTo({e.Expected}));",
            (TestFrameworkKind.NUnit, TrueAssertion t) => $"Assert.That({t.Expression}, Is.True);",
            (TestFrameworkKind.NUnit, FalseAssertion f) => $"Assert.That({f.Expression}, Is.False);",
            (TestFrameworkKind.NUnit, NullAssertion n) => $"Assert.That({n.Expression}, Is.Null);",
            (TestFrameworkKind.NUnit, NotNullAssertion n) => $"Assert.That({n.Expression}, Is.Not.Null);",
            (TestFrameworkKind.NUnit, SameAssertion s) => $"Assert.That({s.Actual}, Is.SameAs({s.Expected}));",
            (TestFrameworkKind.NUnit, ThrowsAssertion t) =>
                $"Assert.Throws<{t.ExceptionType}>({t.ActionExpression});",
            (TestFrameworkKind.MSTest, EqualAssertion e) => $"Assert.AreEqual({e.Expected}, {e.Actual});",
            (TestFrameworkKind.MSTest, TrueAssertion t) => $"Assert.IsTrue({t.Expression});",
            (TestFrameworkKind.MSTest, FalseAssertion f) => $"Assert.IsFalse({f.Expression});",
            (TestFrameworkKind.MSTest, NullAssertion n) => $"Assert.IsNull({n.Expression});",
            (TestFrameworkKind.MSTest, NotNullAssertion n) => $"Assert.IsNotNull({n.Expression});",
            (TestFrameworkKind.MSTest, SameAssertion s) => $"Assert.AreSame({s.Expected}, {s.Actual});",
            (TestFrameworkKind.MSTest, ThrowsAssertion t) =>
                $"Assert.ThrowsException<{t.ExceptionType}>({t.ActionExpression});",
            (TestFrameworkKind.Jest or TestFrameworkKind.Vitest, EqualAssertion e) =>
                $"expect({e.Actual}).toEqual({e.Expected});",
            (TestFrameworkKind.Jest or TestFrameworkKind.Vitest, TrueAssertion t) => $"expect({t.Expression}).toBeTruthy();",
            (TestFrameworkKind.Jest or TestFrameworkKind.Vitest, FalseAssertion f) => $"expect({f.Expression}).toBeFalsy();",
            (TestFrameworkKind.Jest or TestFrameworkKind.Vitest, NullAssertion n) => $"expect({n.Expression}).toBeNull();",
            (TestFrameworkKind.Jest or TestFrameworkKind.Vitest, NotNullAssertion n) =>
                $"expect({n.Expression}).not.toBeNull();",
            (TestFrameworkKind.NodeTest, EqualAssertion e) => $"assert.equal({e.Actual}, {e.Expected});",
            (TestFrameworkKind.NodeTest, TrueAssertion t) => $"assert.ok({t.Expression});",
            (TestFrameworkKind.Pytest or TestFrameworkKind.Unittest, EqualAssertion e) =>
                $"assert {e.Actual} == {e.Expected}",
            (TestFrameworkKind.Pytest or TestFrameworkKind.Unittest, TrueAssertion t) => $"assert {t.Expression}",
            (TestFrameworkKind.Pytest or TestFrameworkKind.Unittest, FalseAssertion f) => $"assert not ({f.Expression})",
            (TestFrameworkKind.Pytest or TestFrameworkKind.Unittest, NullAssertion n) => $"assert {n.Expression} is None",
            (TestFrameworkKind.Pytest or TestFrameworkKind.Unittest, NotNullAssertion n) =>
                $"assert {n.Expression} is not None",
            _ => $"// unsupported assertion {a.Kind} for {framework}"
        };
}
