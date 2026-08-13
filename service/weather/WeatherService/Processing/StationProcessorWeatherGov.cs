using Microsoft.Extensions.Logging;
using WeatherService.Canonical;
using WeatherService.Data;
using WeatherService.Domain;
using WeatherService.Sources;

namespace WeatherService.Processing;

/// <summary>
/// Processes a single US weather station via Weather.gov latest observations.
/// </summary>
public class StationProcessorWeatherGov : StationProcessorBase
{
    private readonly WeatherGovFetcher _fetcher;
    private readonly WeatherGovStationResolver _resolver;
    private readonly WeatherDataRepository _weatherDataRepository;
    private readonly ILogger<StationProcessorWeatherGov> _log;

    public StationProcessorWeatherGov(
        WeatherGovFetcher fetcher,
        WeatherGovStationResolver resolver,
        WeatherDataRepository weatherDataRepository,
        ILogger<StationProcessorWeatherGov> log)
    {
        _fetcher = fetcher;
        _resolver = resolver;
        _weatherDataRepository = weatherDataRepository;
        _log = log;
    }

    protected override async Task ProcessStationAsync(StationRef station, CancellationToken ct)
    {
        string json = await FetchForStationAsync(station, ct).ConfigureAwait(false);
        _log.LogDebug("Saving Weather.gov payload. station={Mli} state={State} bytes={Bytes}",
            station.Mli, station.State, json.Length);
        // Persisted under the WATER gauge's own mli, not the NWS station's id — ows_meteo is keyed by mli.
        await _weatherDataRepository.SaveStationDataAsync(station.Mli, json, WeatherSourceType.WeatherGov, ct).ConfigureAwait(false);
        _log.LogDebug("Processed station. station={Mli} state={State}", station.Mli, station.State);
    }

    protected override async Task VerifyStationAsync(StationRef station, CancellationToken ct)
    {
        string json = await FetchForStationAsync(station, ct).ConfigureAwait(false);
        _log.LogInformation("Startup Weather.gov verification fetched payload. station={Mli} state={State} bytes={Bytes}",
            station.Mli, station.State, json.Length);
    }

    /// <summary>
    /// Resolves the gauge's coordinate to an NWS station (cached), then fetches that station's latest
    /// observation. A gauge with no NWS station nearby surfaces as a skip, exactly like an unpublished
    /// feed would.
    /// </summary>
    private async Task<string> FetchForStationAsync(StationRef station, CancellationToken ct)
    {
        string? nwsStation = await _resolver.ResolveAsync(station, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(nwsStation))
        {
            throw new FileNotFoundException(
                $"Weather.gov has no observation station near station {station.Mli}");
        }

        return await _fetcher.FetchLatestObservationAsync(nwsStation, ct).ConfigureAwait(false);
    }

    protected override ILogger Logger => _log;

    protected override string Country => "US";

    protected override string MissingSourceDescription => "Weather.gov source";
}
