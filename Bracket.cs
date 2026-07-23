namespace Cdp.ScriptableIde;

/// <summary>
/// Structured 0128 locate builder — agent surface without wire-string tax.
/// Emits <c>[F:…;M:…;S:…;K:…]</c> via <see cref="ToWire"/> for harness parse.
/// </summary>
public sealed class Bracket
{
    private string? _file;
    private string? _member;
    private int? _lineStart;
    private int? _lineEnd;
    private string? _scopeKind;
    private int? _scopeIndex;
    private string? _role;

    public static Bracket File(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Bracket { _file = path.Trim() };
    }

    /// <summary>Escape: parse existing wire into a builder (round-trip).</summary>
    public static Bracket Parse(string bracketOrInner)
    {
        var span = BracketLocate.Parse(bracketOrInner);
        return FromSpan(span);
    }

    public static Bracket FromSpan(BracketLocate.Span span) => new()
    {
        _file = span.File,
        _member = span.MemberKey,
        _lineStart = span.LineStart,
        _lineEnd = span.LineEnd,
        _scopeKind = span.ScopeKind,
        _scopeIndex = span.ScopeIndex,
        _role = span.Role
    };

    public Bracket Method(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _member = name.Trim();
        return this;
    }

    /// <summary>Alias of <see cref="Method"/> (types, props, fields share M:).</summary>
    public Bracket Member(string name) => Method(name);

    public Bracket Line(int line1Based) => Lines(line1Based, line1Based);

    public Bracket Lines(int start1Based, int end1Based)
    {
        if (start1Based < 1)
            throw new ArgumentOutOfRangeException(nameof(start1Based));
        if (end1Based < start1Based)
            throw new ArgumentOutOfRangeException(nameof(end1Based));
        _lineStart = start1Based;
        _lineEnd = end1Based;
        return this;
    }

    public Bracket If(int index1Based = 1) => Scope("if", index1Based);
    public Bracket For(int index1Based = 1) => Scope("for", index1Based);
    public Bracket Foreach(int index1Based = 1) => Scope("foreach", index1Based);
    public Bracket While(int index1Based = 1) => Scope("while", index1Based);

    public Bracket Scope(string kind, int index1Based = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (index1Based < 1)
            throw new ArgumentOutOfRangeException(nameof(index1Based));
        _scopeKind = kind.Trim().ToLowerInvariant();
        _scopeIndex = index1Based;
        return this;
    }

    public Bracket Role(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        _role = role.Trim();
        return this;
    }

    public Bracket Condition() => Role("Condition");
    public Bracket Then() => Role("Branch.True");
    public Bracket Else() => Role("Branch.False");

    public BracketLocate.Span ToSpan() =>
        new(_file, _member, _lineStart, _lineEnd, _scopeKind, _scopeIndex, _role);

    public string ToWire() => BracketLocate.Format(ToSpan());

    public override string ToString() => ToWire();

    public static implicit operator string(Bracket bracket) => bracket.ToWire();

    public static implicit operator BracketLocate.Span(Bracket bracket) => bracket.ToSpan();
}
