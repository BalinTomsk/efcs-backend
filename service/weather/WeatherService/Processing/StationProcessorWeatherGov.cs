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
    private readonly WeatherGovConverter _converter;
    private readonly ILogger<StationProcessorWeatherGov> _log;

    public StationProcessorWeatherGov(
        WeatherGovFetcher fetcher,
        WeatherGovStationResolver resolver,
        WeatherDataRepository weatherDataRepository,
        WeatherGovConverter converter,
        ILogger<StationProcessorWeatherGov> log)
    {
        _fetcher = fetcher;
        _resolver = resolver;
        _weatherDataRepository = weatherDataRepository;
        _converter = converter;
        _log = log;
    }

    protected override async Task ProcessStationAsync(StationRef station, CancellationToken ct)
    {
        string json = await FetchForStationAsync(station, ct).ConfigureAwait(false);
        // Convert HERE, not in the database. A shape the converter does not recognise throws and is
        // counted as a failed station; the old T-SQL parser could only fail silently.
        CanonicalForecast forecast = _converter.Convert(json, station.Mli);
        string canonical = forecast.ToJson();
        _log.LogDebug("Saving Weather.gov payload. station={Mli} state={State} days={Days} bytes={Bytes}",
            station.Mli, station.State, forecast.Days.Count, canonical.Length);
        // Persisted under the WATER gauge's own mli, not the NWS grid cell - ows_meteo is keyed by mli.
        await _weatherDataRepository
            .SaveStationDataAsync(station.Mli, canonical, _converter.ProviderType, ct)
            .ConfigureAwait(false);
        _log.LogDebug("Processed station. station={Mli} state={State}", station.Mli, station.State);
    }

    protected override async Task VerifyStationAsync(StationRef station, CancellationToken ct)
    {
        string json = await FetchForStationAsync(station, ct).ConfigureAwait(false);
        _log.LogInformation("Startup Weather.gov verification fetched payload. station={Mli} state={State} bytes={Bytes}",
            station.Mli, station.State, json.Length);
    }

    /// <summary>
    /// Fetches the gauge coordinate's GRIDPOINT FORECAST — the multi-day forecast, not the latest
    /// observation. A coordinate outside NWS coverage surfaces as a skip, exactly like an unpublished
    /// feed would.
    ///
    /// <para>The nearest-station resolver is no longer on this path: a forecast is keyed by grid cell,
    /// not by observation station. <c>WeatherGovStationResolver</c> and its
    /// <c>dbo.weather_gov_station</c> cache are left in place for the observation endpoint but now have
    /// no caller here.</para>
    /// </summary>
    private async Task<string> FetchForStationAsync(StationRef station, CancellationToken ct)
    {
        string? json = await _fetcher
            .FetchGridpointForecastAsync(station.Latitude, station.Longitude, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FileNotFoundException(
                $"Weather.gov publishes no gridpoint forecast for station {station.Mli}");
        }
        return json;
    }

    protected override ILogger Logger => _log;

    protected override string Country => "US";

    protected override string MissingSourceDescription => "Weather.gov source";
}
