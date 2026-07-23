namespace Cdp.ScriptableIde;

/// <summary>
/// Declare intents — agent owns facts (name / type / value); harness owns syntax.
/// </summary>
public static class Declare
{
    /// <summary><c>Declare.Variable.Name("n").Type(Types.Integer).Value("1")</c></summary>
    public static class Variable
    {
        public static DeclareBuilder Name(string name) => new DeclareBuilder(isConstant: false).Name(name);
    }

    /// <summary><c>Declare.Constant.Name("Max").Type(Types.Integer).Value("10")</c> — lang projects const/Final.</summary>
    public static class Constant
    {
        public static DeclareBuilder Name(string name) => new DeclareBuilder(isConstant: true).Name(name);
    }
}

public sealed class DeclareBuilder(bool isConstant)
{
    private string? _name;
    private TypeIntent? _type;
    private string? _valueWire;
    private ExprIntent? _valueExpr;

    public DeclareBuilder Name(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name.Trim();
        return this;
    }

    public DeclareBuilder Type(TypeIntent type)
    {
        ArgumentNullException.ThrowIfNull(type);
        _type = type;
        return this;
    }

    /// <summary>RHS expression wire (escape) — prefer <see cref="Value(ExprIntent)"/>.</summary>
    public DeclareBuilder Value(string exprWire)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exprWire);
        _valueWire = exprWire.Trim();
        _valueExpr = null;
        return this;
    }

    /// <summary>RHS via closed <see cref="ExprIntent"/> (harness projects).</summary>
    public DeclareBuilder Value(ExprIntent expr)
    {
        ArgumentNullException.ThrowIfNull(expr);
        _valueExpr = expr;
        _valueWire = null;
        return this;
    }

    public ArrangeIntent ToArrange()
    {
        if (string.IsNullOrWhiteSpace(_name))
            throw new InvalidOperationException(
                isConstant ? "Declare.Constant.Name is required" : "Declare.Variable.Name is required");
        if (_type is null)
            throw new InvalidOperationException(
                isConstant ? "Declare.Constant.Type is required" : "Declare.Variable.Type is required");
        if (isConstant && _valueExpr is null && string.IsNullOrWhiteSpace(_valueWire))
            throw new InvalidOperationException("Declare.Constant.Value is required");
        return new DeclareArrange(_name!, _type, _valueWire, isConstant, _valueExpr);
    }
}
