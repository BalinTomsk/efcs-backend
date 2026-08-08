using Microsoft.Extensions.Logging;
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
    private readonly ILogger<StationProcessorOpen> _log;

    public StationProcessorOpen(
        OpenMeteoFetcher fetcher,
        WeatherDataRepository weatherDataRepository,
        ILogger<StationProcessorOpen> log)
    {
        _fetcher = fetcher;
        _weatherDataRepository = weatherDataRepository;
        _log = log;
    }

    protected override async Task ProcessStationAsync(StationRef station, CancellationToken ct)
    {
        string json = await _fetcher.FetchAsync(station.Latitude, station.Longitude, ct).ConfigureAwait(false);
        _log.LogDebug("Saving Open-Meteo payload. station={Mli} state={State} bytes={Bytes}",
            station.Mli, station.State, json.Length);
        await _weatherDataRepository.SaveStationDataAsync(station.Mli, json, ct).ConfigureAwait(false);
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
