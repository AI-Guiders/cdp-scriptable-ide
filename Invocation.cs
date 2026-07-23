namespace Cdp.ScriptableIde;

/// <summary>One call argument — Name null = positional. Types are projection (not stored here).</summary>
public sealed record CallArg(string? Name, string ExprWire);

/// <summary>
/// Invocation entity — Receiver / Name / Params / Bind.
/// Short path remains <see cref="Act.Call"/>; use this when arity / named args matter.
/// </summary>
public sealed class InvocationEntity
{
    private string? _receiver;
    private string? _name;
    private string? _bind;
    private readonly List<CallArg> _args = [];

    public InvocationEntity On(string receiver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiver);
        _receiver = receiver.Trim();
        return this;
    }

    public InvocationEntity Named(string methodOrMember)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodOrMember);
        _name = methodOrMember.Trim();
        return this;
    }

    /// <summary>Positional argument (expression wire — no type field).</summary>
    public InvocationEntity Arg(string exprWire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exprWire);
        _args.Add(new CallArg(null, exprWire.Trim()));
        return this;
    }

    /// <summary>Named argument — projection emits lang form (csharp <c>name: expr</c>, py <c>name=expr</c>).</summary>
    public InvocationEntity Arg(string name, string exprWire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(exprWire);
        _args.Add(new CallArg(name.Trim(), exprWire.Trim()));
        return this;
    }

    public InvocationEntity Bind(string local)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(local);
        _bind = local.Trim();
        return this;
    }

    public ActIntent ToAct()
    {
        if (string.IsNullOrWhiteSpace(_receiver))
            throw new InvalidOperationException("Invocation.On(receiver) is required");
        if (string.IsNullOrWhiteSpace(_name))
            throw new InvalidOperationException("Invocation.Named(method) is required");
        return new CallAct(_receiver!, _name!, _args.ToArray(), _bind);
    }
}

public static class Invocation
{
    public static InvocationEntity On(string receiver) => new InvocationEntity().On(receiver);

    /// <summary>Start from method Anchor — resolves to Type.Method (static) unless <see cref="InvocationEntity.On"/> set.</summary>
    public static CallBuilder At(Anchor methodAnchor) => Act.Call(methodAnchor);
}
