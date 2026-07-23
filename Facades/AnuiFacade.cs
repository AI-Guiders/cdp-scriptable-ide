namespace Cdp.ScriptableIde;

public sealed class AnuiFacade(IScriptToolBus bus)
{
    public Task<string> ListAdaptersAsync(CancellationToken ct = default) =>
        bus.InvokeAsync("anui", "anui_list_adapters", ScriptArgs.From(new { }), ct);

    public Task<string> AuditAsync(CancellationToken ct = default) =>
        bus.InvokeAsync("anui", "anui_audit", ScriptArgs.From(new { }), ct);
}
