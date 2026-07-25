namespace Cdp.ScriptableIde;

/// <summary>
/// Locate <b>entity</b> (locus) — agent surface without wire-string tax.
/// Bracket notation <c>[F:…;M:…;S:…;K:…]</c> is only the wire projection via <see cref="ToWire"/>.
/// </summary>
public sealed class Anchor
{
    private string? _file;
    private string? _member;
    private int? _lineStart;
    private int? _lineEnd;
    private string? _scopeKind;
    private int? _scopeIndex;
    private string? _role;
    private string? _xmlPath;
    private string? _attr;
    private string? _family;
    private string? _command;
    private string? _go;
    private Anchor? _nested;

    public static Anchor File(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Anchor { _file = path.Trim() };
    }

    /// <summary>Escape: parse bracket wire into an Anchor (round-trip).</summary>
    public static Anchor Parse(string bracketWireOrInner)
    {
        var span = BracketLocate.Parse(bracketWireOrInner);
        return FromSpan(span);
    }

    public static Anchor FromSpan(BracketLocate.Span span) => new()
    {
        _file = span.File,
        _member = span.MemberKey,
        _lineStart = span.LineStart,
        _lineEnd = span.LineEnd,
        _scopeKind = span.ScopeKind,
        _scopeIndex = span.ScopeIndex,
        _role = span.Role,
        _xmlPath = span.XmlPath,
        _attr = span.Attr,
        _family = span.Family,
        _command = span.Command,
        _go = span.Go,
        _nested = span.NestedAnchor is { } n ? FromSpan(n) : null
    };

    /// <summary>XML element path — <c>X:Project/PropertyGroup/OutputType</c>.</summary>
    public Anchor Element(string xmlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlPath);
        _xmlPath = xmlPath.Trim();
        return this;
    }

    /// <summary>XML attribute — <c>Attribute:Version</c> (alias <c>A:</c>).</summary>
    public Anchor Attr(string attributeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attributeName);
        _attr = attributeName.Trim();
        return this;
    }

    /// <summary><c>Family:code|xml|navigation</c>.</summary>
    public Anchor Family(string family)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        _family = family.Trim();
        return this;
    }

    public Anchor Navigation() => Family("navigation");
    public Anchor CodeFamily() => Family("code");
    public Anchor XmlFamily() => Family("xml");

    /// <summary>Navigation <c>Command:open|goto|restore|show|go</c>.</summary>
    public Anchor Command(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        _command = command.Trim().ToLowerInvariant();
        _family ??= "navigation";
        return this;
    }

    /// <summary>Navigation organ — <c>Go:editor_scene</c> with <c>Command:go</c>.</summary>
    public Anchor Go(string organOrScene)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organOrScene);
        _go = organOrScene.Trim();
        _family ??= "navigation";
        _command ??= "go";
        return this;
    }

    /// <summary>Nested locus Anchor — <c>Anchor:[…]</c>.</summary>
    public Anchor Nested(Anchor locus)
    {
        ArgumentNullException.ThrowIfNull(locus);
        _nested = locus;
        _family ??= "navigation";
        return this;
    }

    public static Anchor LandRestore() => new Anchor().Navigation().Command("restore");

    public static Anchor LandShow(string path) =>
        new Anchor().Navigation().Command("show").Nested(File(path));

    public static Anchor LandOpen(string path, int? line = null)
    {
        var inner = File(path).CodeFamily();
        if (line is > 0)
            inner.Line(line.Value);
        return new Anchor().Navigation().Command("open").Nested(inner);
    }

    public static Anchor LandGoto(Anchor codeLocus) =>
        new Anchor().Navigation().Command("goto").Nested(
            string.IsNullOrWhiteSpace(codeLocus.ToSpan().Family)
                ? codeLocus.CodeFamily()
                : codeLocus);

    public static Anchor LandGo(string organ, Anchor? locus = null)
    {
        var a = new Anchor().Navigation().Command("go").Go(organ);
        if (locus is not null)
            a.Nested(locus);
        return a;
    }

    /// <summary>Upsert missing XML element under parent — <c>K:Element</c>.</summary>
    public Anchor CreateElement() => Role("Element");

    public Anchor Method(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _member = name.Trim();
        return this;
    }

    /// <summary>Alias of <see cref="Method"/> (types, props, fields share M:).</summary>
    public Anchor Member(string name) => Method(name);

    public Anchor Line(int line1Based) => Lines(line1Based, line1Based);

    public Anchor Lines(int start1Based, int end1Based)
    {
        if (start1Based < 1)
            throw new ArgumentOutOfRangeException(nameof(start1Based));
        if (end1Based < start1Based)
            throw new ArgumentOutOfRangeException(nameof(end1Based));
        _lineStart = start1Based;
        _lineEnd = end1Based;
        return this;
    }

    public Anchor If(int index1Based = 1) => Scope("if", index1Based);
    public Anchor For(int index1Based = 1) => Scope("for", index1Based);
    public Anchor Foreach(int index1Based = 1) => Scope("foreach", index1Based);
    public Anchor While(int index1Based = 1) => Scope("while", index1Based);

    public Anchor Scope(string kind, int index1Based = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (index1Based < 1)
            throw new ArgumentOutOfRangeException(nameof(index1Based));
        _scopeKind = kind.Trim().ToLowerInvariant();
        _scopeIndex = index1Based;
        return this;
    }

    public Anchor Role(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        _role = role.Trim();
        return this;
    }

    public Anchor Condition() => Role("Condition");
    public Anchor Then() => Role("Branch.True");
    public Anchor Else() => Role("Branch.False");

    /// <summary>foreach <c>in</c> expression (foreach has no boolean Condition).</summary>
    public Anchor Expression() => Role("Expression");

    /// <summary>Alias of <see cref="Expression"/> for foreach collection.</summary>
    public Anchor Collection() => Role("Collection");

    /// <summary>RHS of <c>var x = …</c> / field init / assignment (L:+K:Initializer).</summary>
    public Anchor Initializer() => Role("Initializer");

    /// <summary>Method/ctor parameter by name — <c>K:Parameter:a</c>.</summary>
    public Anchor Parameter(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Role("Parameter:" + name.Trim());
    }

    /// <summary>Identifier token of member/type/local — <c>K:Name</c>.</summary>
    public Anchor Name() => Role("Name");

    /// <summary>Method/property return type — <c>K:ReturnType</c>.</summary>
    public Anchor ReturnType() => Role("ReturnType");

    /// <summary>Method/accessor body — <c>K:Body</c>.</summary>
    public Anchor Body() => Role("Body");

    /// <summary>Type of local/field/param — <c>K:Type</c>.</summary>
    public Anchor Type() => Role("Type");

    public BracketLocate.Span ToSpan() =>
        new(_file, _member, _lineStart, _lineEnd, _scopeKind, _scopeIndex, _role, _xmlPath, _attr,
            _family, _command, _go, _nested?.ToSpan());

    /// <summary>Bracket-notation wire for harness parse.</summary>
    public string ToWire() => BracketLocate.Format(ToSpan());

    public override string ToString() => ToWire();

    public static implicit operator string(Anchor anchor) => anchor.ToWire();

    public static implicit operator BracketLocate.Span(Anchor anchor) => anchor.ToSpan();
}
