namespace Cdp.ScriptableIde;

/// <summary>Closed assertion intents (W3) — projection owns assert syntax.</summary>
public abstract record AssertionIntent(string Kind);

public sealed record EqualAssertion(string Actual, string Expected) : AssertionIntent("equal");
public sealed record TrueAssertion(string Expression) : AssertionIntent("true");
public sealed record FalseAssertion(string Expression) : AssertionIntent("false");
public sealed record NullAssertion(string Expression) : AssertionIntent("null");
public sealed record NotNullAssertion(string Expression) : AssertionIntent("not_null");
public sealed record SameAssertion(string Actual, string Expected) : AssertionIntent("same");
public sealed record ThrowsAssertion(string ExceptionType, string ActionExpression) : AssertionIntent("throws");

/// <summary>Factory for closed assertion catalog (lang/FW via harness projection).</summary>
public static class Assertion
{
    public static AssertionIntent Equal(string actual, string expected) => new EqualAssertion(actual, expected);
    public static AssertionIntent True(string expression) => new TrueAssertion(expression);
    public static AssertionIntent False(string expression) => new FalseAssertion(expression);
    public static AssertionIntent Null(string expression) => new NullAssertion(expression);
    public static AssertionIntent NotNull(string expression) => new NotNullAssertion(expression);
    public static AssertionIntent Same(string actual, string expected) => new SameAssertion(actual, expected);
    public static AssertionIntent Throws(string exceptionType, string actionExpression) =>
        new ThrowsAssertion(exceptionType, actionExpression);
}

/// <summary>Closed arrange intents — harness owns <c>var</c> / ctor wire.</summary>
public abstract record ArrangeIntent(string Kind);

/// <summary>Bind local to <c>new {SutType}(args)</c> — type resolved at Apply.</summary>
public sealed record SutArrange(string Local, string? ArgsWire) : ArrangeIntent("sut");

/// <summary>Bind local to <c>new TypeName(args)</c>.</summary>
public sealed record NewArrange(string Local, string TypeName, string? ArgsWire) : ArrangeIntent("new");

/// <summary>Escape — raw statement(s) without trailing semicolon requirement (harness may add).</summary>
public sealed record StmtArrange(string Code) : ArrangeIntent("stmt");

/// <summary>Typed local / const from <see cref="Declare"/> — type via closed <see cref="TypeIntent"/>.</summary>
public sealed record DeclareArrange(
    string Local,
    TypeIntent Type,
    string? ValueWire,
    bool IsConstant,
    ExprIntent? ValueExpr = null) : ArrangeIntent(IsConstant ? "const" : "var");

public static class Arrange
{
    public static ArrangeIntent Sut(string local, string? argsWire = null) => new SutArrange(local, argsWire);
    public static ArrangeIntent New(string local, string typeName, string? argsWire = null) =>
        new NewArrange(local, typeName, argsWire);

    /// <summary>Ctor bind with closed type: <c>Arrange.New("c", Types.Of("Counter"), "3")</c>.</summary>
    public static ArrangeIntent New(string local, TypeIntent type, string? argsWire = null) =>
        new TypedNewArrange(local, type, argsWire);

    public static ArrangeIntent Stmt(string code) => new StmtArrange(code);

    /// <summary>From <see cref="Declare.Variable"/> / <see cref="Declare.Constant"/> builder.</summary>
    public static ArrangeIntent From(DeclareBuilder builder) => builder.ToArrange();
}

/// <summary><c>new T(args)</c> with <see cref="TypeIntent"/> (no string type wire).</summary>
public sealed record TypedNewArrange(string Local, TypeIntent Type, string? ArgsWire) : ArrangeIntent("typed_new");

/// <summary>Closed act intents — harness owns call / bind wire.</summary>
public abstract record ActIntent(string Kind);

/// <summary>Call — Args empty or positional/named. Prefer <see cref="Act.Call(Anchor)"/> over string names.</summary>
public sealed record CallAct(
    string? Receiver,
    string? Method,
    IReadOnlyList<CallArg> Args,
    string? Bind,
    string? MethodAnchor = null) : ActIntent("call");

public sealed record StmtAct(string Code) : ActIntent("stmt");

/// <summary>Fluent call from method <see cref="Anchor"/> — Arg / Bind / On(instance).</summary>
public sealed class CallBuilder
{
    private readonly string _methodAnchor;
    private string? _instanceReceiver;
    private string? _bind;
    private readonly List<CallArg> _args = [];

    internal CallBuilder(string methodAnchor) => _methodAnchor = methodAnchor;

    /// <summary>Instance receiver local (omit for static Type.Method projection).</summary>
    public CallBuilder On(string instanceLocal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceLocal);
        _instanceReceiver = instanceLocal.Trim();
        return this;
    }

    public CallBuilder Arg(string exprWire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exprWire);
        _args.Add(new CallArg(null, exprWire.Trim()));
        return this;
    }

    public CallBuilder Arg(string name, string exprWire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(exprWire);
        _args.Add(new CallArg(name.Trim(), exprWire.Trim()));
        return this;
    }

    public CallBuilder Bind(string local)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(local);
        _bind = local.Trim();
        return this;
    }

    public ActIntent ToAct() =>
        new CallAct(_instanceReceiver, Method: null, _args.ToArray(), _bind, _methodAnchor);
}

public static class Act
{
    /// <summary>Locate method via Anchor — preferred over string receiver/method.</summary>
    public static CallBuilder Call(Anchor methodAnchor)
    {
        ArgumentNullException.ThrowIfNull(methodAnchor);
        return new CallBuilder(methodAnchor.ToWire());
    }

    public static CallBuilder CallMethod(string methodAnchorWire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodAnchorWire);
        return new CallBuilder(methodAnchorWire.Trim());
    }

    /// <summary>Wire escape — prefer <see cref="Call(Anchor)"/>.</summary>
    public static ActIntent Call(string receiver, string method, string? argsWire = null, string? bind = null)
    {
        CallArg[] args = string.IsNullOrWhiteSpace(argsWire)
            ? []
            : [new CallArg(null, argsWire.Trim())];
        return new CallAct(receiver, method, args, bind);
    }

    /// <summary>Rich path — <see cref="Invocation"/> entity.</summary>
    public static ActIntent Invoke(InvocationEntity invocation) => invocation.ToAct();

    public static ActIntent Stmt(string code) => new StmtAct(code);
}
