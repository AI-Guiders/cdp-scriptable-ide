namespace Cdp.ScriptableIde;

/// <summary>
/// CSX explore help — live from XML doc comments on the allowlisted surface.
/// </summary>
public sealed class HelpFacade
{
    /// <summary>List ScriptGlobals facades (+ summaries when XML docs are present).</summary>
    public string Toc(int maxFacades = 48) => CsxHelpCatalog.Toc(maxFacades);

    /// <summary>
    /// Members for a path: <c>Symbol</c>, <c>SemanticMap</c>, <c>Symbol.Named</c>, <c>Help</c>.
    /// </summary>
    public string Of(string path, int maxMembers = 40) => CsxHelpCatalog.Of(path, maxMembers);
}
