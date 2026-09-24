using WaterApi.Domain;

namespace WaterApi.Data;

/// <summary>
/// No-database backing (used when <c>DB_URL</c> is not configured): returns no stations. Lets the service
/// start, answer every endpoint and pass its health probe on a workstation with zero DB config — the same
/// role docapi's in-memory stores play under its default profile.
/// </summary>
public sealed class InMemoryStationQueryRepository : IStationQueryRepository
{
    public Task<IReadOnlyList<MapStation>> FindMapStationsAsync(
        string country, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MapStation>>(Array.Empty<MapStation>());
}
