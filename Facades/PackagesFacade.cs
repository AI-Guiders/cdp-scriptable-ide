namespace Cdp.ScriptableIde;

/// <summary>CSX brand: <c>Packages.Find/Add/Remove/…</c> — NuGet or npm by session language.</summary>
public sealed class PackagesFacade(ScriptToolBus bus, PlanContext plan)
{
    public Task<StepResponse> FindAsync(string query, int take = 5, CancellationToken ct = default) =>
        PackageOps.FindAsync(bus, plan, query, take, ct);

    public Task<StepResponse> ListAsync(CancellationToken ct = default) =>
        PackageOps.ListAsync(bus, plan, null, ct);

    public Task<StepResponse> AddAsync(string packageId, string? version = null, CancellationToken ct = default) =>
        PackageOps.AddAsync(bus, plan, packageId, version, null, ct);

    public Task<StepResponse> RemoveAsync(string packageId, CancellationToken ct = default) =>
        PackageOps.RemoveAsync(bus, plan, packageId, null, ct);

    public Task<StepResponse> UpdateAsync(string packageId, string? version = null, CancellationToken ct = default) =>
        PackageOps.UpdateAsync(bus, plan, packageId, version, null, ct);

    public Task<StepResponse> OutdatedAsync(CancellationToken ct = default) =>
        PackageOps.OutdatedAsync(bus, plan, null, ct);
}
