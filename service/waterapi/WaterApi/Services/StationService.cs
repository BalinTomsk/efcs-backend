using WaterApi.Domain;

namespace WaterApi.Services;

/// <summary>
/// Station read operations for the frontend map. Validates every client input up front (so bad requests
/// are 400s that never reach the cache or the database) and answers from <see cref="StationMapCache"/>.
/// </summary>
public class StationService
{
    private readonly StationMapCache _cache;

    public StationService(StationMapCache cache)
    {
        _cache = cache;
    }

    /// <summary>
    /// The country's map pins, optionally narrowed to one province/state.
    /// </summary>
    /// <param name="country">ISO-2 country, case-insensitive; must be one of the cache's countries.</param>
    /// <param name="state">Optional two-letter province/state, case-insensitive.</param>
    public async Task<StationMapView> GetMapAsync(string? country, string? state, CancellationToken cancellationToken)
    {
        string normalizedCountry = NormalizeCountry(country);
        string? normalizedState = NormalizeState(state);

        StationMapSnapshot snapshot = await _cache.GetAsync(normalizedCountry, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<MapStation> stations = normalizedState is null
            ? snapshot.Stations
            : snapshot.Stations.Where(s => s.State == normalizedState).ToList();

        return new StationMapView(snapshot, normalizedState, stations, _cache.IsStale(snapshot));
    }

    /// <summary>
    /// One station by sid, searched across every cached country. Only stations that are on the map
    /// (<c>vMapView</c>) are found.
    /// </summary>
    /// <exception cref="StationNotFoundException">No cached country has the sid.</exception>
    public async Task<MapStation> GetStationAsync(int sid, CancellationToken cancellationToken)
    {
        if (sid <= 0)
        {
            throw new InvalidRequestException("sid must be a positive integer");
        }

        foreach (string country in _cache.Countries)
        {
            StationMapSnapshot snapshot = await _cache.GetAsync(country, cancellationToken).ConfigureAwait(false);
            if (snapshot.BySid.TryGetValue(sid, out MapStation? station))
            {
                return station;
            }
        }

        throw new StationNotFoundException(sid);
    }

    private string NormalizeCountry(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            throw new InvalidRequestException("country is required (" + string.Join(" or ", _cache.Countries) + ")");
        }

        string upper = country.Trim().ToUpperInvariant();
        if (!_cache.Countries.Contains(upper, StringComparer.Ordinal))
        {
            throw new InvalidRequestException(
                $"country must be one of {string.Join(", ", _cache.Countries)}; got '{country.Trim()}'");
        }

        return upper;
    }

    private static string? NormalizeState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        string upper = state.Trim().ToUpperInvariant();
        if (upper.Length != 2 || !char.IsAsciiLetterUpper(upper[0]) || !char.IsAsciiLetterUpper(upper[1]))
        {
            throw new InvalidRequestException($"state must be a two-letter code; got '{state.Trim()}'");
        }

        return upper;
    }
}

/// <summary>A filtered view of one snapshot, as returned to the controller.</summary>
public sealed record StationMapView(
    StationMapSnapshot Snapshot,
    string? State,
    IReadOnlyList<MapStation> Stations,
    bool Stale)
{
    public string ETag => Snapshot.ETagFor(State);
}

/// <summary>Client input error (→ HTTP 400).</summary>
public sealed class InvalidRequestException(string message) : Exception(message);

/// <summary>Unknown station (→ HTTP 404).</summary>
public sealed class StationNotFoundException(int sid) : Exception($"Station {sid} is not on the map");
