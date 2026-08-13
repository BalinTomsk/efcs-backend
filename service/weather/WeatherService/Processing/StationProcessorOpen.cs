using Microsoft.Extensions.Logging;
using WeatherService.Canonical;
using WeatherService.Data;
using WeatherService.Domain;
using WeatherService.Sources;

namespace WeatherService.Processing;

/// <summary>
/// Processes a single station via Open-Meteo.
/// </summary>
public class StationProcessorOpen : StationProcessorBase
{
    private readonly OpenMeteoFetcher _fetcher;
    private readonly WeatherDataRepository _weatherDataRepository;
    private readonly OpenMeteoConverter _converter;
    private readonly ILogger<StationProcessorOpen> _log;

    public StationProcessorOpen(
        OpenMeteoFetcher fetcher,
        WeatherDataRepository weatherDataRepository,
        OpenMeteoConverter converter,
        ILogger<StationProcessorOpen> log)
    {
        _fetcher = fetcher;
        _weatherDataRepository = weatherDataRepository;
        _converter = converter;
        _log = log;
    }

    protected override async Task ProcessStationAsync(StationRef station, CancellationToken ct)
    {
        string json = await _fetcher.FetchAsync(station.Latitude, station.Longitude, ct).ConfigureAwait(false);
        _log.LogDebug("Saving Open-Meteo payload. station={Mli} state={State} bytes={Bytes}",
            station.Mli, station.State, json.Length);
        // Convert HERE, not in the database. A shape the converter does not recognise throws
        // and is counted as a failed station; the old T-SQL parser could only fail silently.
        CanonicalForecast forecast = _converter.Convert(json, station.Mli);
        string canonical = forecast.ToJson();
        await _weatherDataRepository
            .SaveStationDataAsync(station.Mli, canonical, _converter.ProviderType, ct)
            .ConfigureAwait(false);
        _log.LogDebug("Processed station. station={Mli} state={State}", station.Mli, station.State);
    }

    protected override async Task VerifyStationAsync(StationRef station, CancellationToken ct)
    {
        string json = await _fetcher.FetchAsync(station.Latitude, station.Longitude, ct).ConfigureAwait(false);
        _log.LogInformation("Startup Open-Meteo verification fetched payload. station={Mli} state={State} bytes={Bytes}",
            station.Mli, station.State, json.Length);
    }

    protected override ILogger Logger => _log;

    protected override string Country => "US";

    protected override string MissingSourceDescription => "Open-Meteo source";
}
