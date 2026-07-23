namespace Cdp.ScriptableIde;

/// <summary>
/// Vendor catalog endpoints for TFM/engine/package resolvers.
/// SSOT for URLs = host config (<c>[vendor_catalog]</c> in cdp-mcp.toml); code only reads <see cref="Current"/>.
/// </summary>
public sealed class VendorCatalogOptions
{
    /// <summary>.NET releases-index mirrors (first success wins). Field <c>release-type</c> = lts|sts.</summary>
    public IReadOnlyList<string> DotnetReleasesIndexUrls { get; init; } = [];

    /// <summary>Node dist index. Field <c>lts</c> = codename string | false.</summary>
    public string NodeDistIndexUrl { get; init; } = "";

    /// <summary>NuGet query URL template; placeholders <c>{query}</c>, <c>{take}</c>.</summary>
    public string NugetSearchUrl { get; init; } = "";

    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromHours(6);
}

/// <summary>Process-wide vendor catalog options (configured by CDP host from TOML).</summary>
public static class VendorCatalog
{
    private static VendorCatalogOptions _current = CreateBuiltInDefaults();

    public static VendorCatalogOptions Current => _current;

    public static void Configure(VendorCatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DotnetReleasesIndexUrls.Count == 0)
            throw new ArgumentException("DotnetReleasesIndexUrls must not be empty.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.NodeDistIndexUrl))
            throw new ArgumentException("NodeDistIndexUrl is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.NugetSearchUrl) ||
            !options.NugetSearchUrl.Contains("{query}", StringComparison.Ordinal))
            throw new ArgumentException("NugetSearchUrl must include {query} placeholder.", nameof(options));
        _current = options;
    }

    /// <summary>
    /// Built-in defaults when host omits <c>[vendor_catalog]</c> — same values as sample TOML.
    /// Prefer overriding via config rather than editing call sites.
    /// </summary>
    public static VendorCatalogOptions CreateBuiltInDefaults() => new()
    {
        DotnetReleasesIndexUrls =
        [
            "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json",
            "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json"
        ],
        NodeDistIndexUrl = "https://nodejs.org/dist/index.json",
        NugetSearchUrl =
            "https://azuresearch-usnc.nuget.org/query?q={query}&take={take}&prerelease=false",
        CacheTtl = TimeSpan.FromHours(6)
    };

    public static string FormatNugetSearch(string query, int take)
    {
        var tmpl = Current.NugetSearchUrl;
        return tmpl
            .Replace("{query}", Uri.EscapeDataString(query), StringComparison.Ordinal)
            .Replace("{take}", take.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }
}
