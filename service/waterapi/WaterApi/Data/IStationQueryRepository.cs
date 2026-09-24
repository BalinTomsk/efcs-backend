using WaterApi.Domain;

namespace WaterApi.Data;

/// <summary>
/// Read access to the map's station points. Two implementations, chosen at startup by
/// <see cref="Configuration.ServiceRegistration"/> (the C# counterpart of docapi's default-vs-<c>jdbc</c>
/// profile): <see cref="SqlStationQueryRepository"/> when <c>DB_URL</c> is set, otherwise
/// <see cref="InMemoryStationQueryRepository"/> so the service starts and serves with no database.
/// </summary>
public interface IStationQueryRepository
{
    /// <summary>
    /// Returns every map station for the country (<c>CA</c> / <c>US</c>), ordered by sid.
    /// </summary>
    Task<IReadOnlyList<MapStation>> FindMapStationsAsync(string country, CancellationToken cancellationToken = default);
}
