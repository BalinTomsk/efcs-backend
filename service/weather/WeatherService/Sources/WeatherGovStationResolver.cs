using Microsoft.Extensions.Logging;
using WeatherService.Data;
using WeatherService.Domain;

namespace WeatherService.Sources;

/// <summary>
/// Answers "which NWS station serves this water gauge", caching the answer in the database.
///
/// <para><c>WaterStation.MLI</c> is a water-gauge identifier — a USGS site number for US rows — never
/// an NWS call sign, so asking Weather.gov for observations by <c>mli</c> 404s for every US station.
/// The gauge's coordinate resolves to a real station instead.</para>
///
/// <para>The mapping is geographic and effectively permanent, so it is resolved once per gauge and
/// stored: doing it inline every cycle would double the request count against a rate-limited public
/// API for no benefit. A "no station nearby" answer is cached too — otherwise every cycle would
/// re-ask a point that will never resolve.</para>
/// </summary>
public class WeatherGovStationResolver
{
    private readonly WeatherGovFetcher _fetcher;
    private readonly WeatherGovStationRepository _repository;
    private readonly ILogger<WeatherGovStationResolver> _log;

    public WeatherGovStationResolver(
        WeatherGovFetcher fetcher,
        WeatherGovStationRepository repository,
        ILogger<WeatherGovStationResolver> log)
    {
        _fetcher = fetcher;
        _repository = repository;
        _log = log;
    }

    /// <summary>
    /// The NWS station id to fetch observations from, or <c>null</c> when there is none nearby.
    /// </summary>
    public virtual async Task<string?> ResolveAsync(StationRef station, CancellationToken ct = default)
    {
        WeatherGovStationRepository.CachedStation? cached =
            await _repository.FindAsync(station.Mli, ct).ConfigureAwait(false);

        if (cached is { } hit)
        {
            // A row exists, so the question was already asked — including when the answer was "none".
            return hit.StationId;
        }

        string? resolved = await _fetcher
            .FindNearestStationAsync(station.Latitude, station.Longitude, ct)
            .ConfigureAwait(false);

        // Cache the miss as well as the hit; both are answers.
        await _repository.SaveAsync(station.Mli, station.Latitude, station.Longitude, resolved, ct)
            .ConfigureAwait(false);

        if (resolved is null)
        {
            _log.LogInformation(
                "No Weather.gov station near station. station={Mli} state={State} lat={Lat} lon={Lon}",
                station.Mli, station.State, station.Latitude, station.Longitude);
        }
        else
        {
            _log.LogInformation(
                "Resolved Weather.gov station for gauge. station={Mli} state={State} nwsStation={NwsStation}",
                station.Mli, station.State, resolved);
        }

        return resolved;
    }
}
