using Microsoft.Extensions.Logging;
using WeatherService.Data;
using WeatherService.Domain;
using WeatherService.Sources;

namespace WeatherService.Processing;

/// <summary>
/// Processes a single Canadian station via Weather Canada SWOB observations.
/// </summary>
public class StationProcessorWeatherCanada : StationProcessorBase
{
    private readonly WeatherCanadaFetcher _fetcher;
    private readonly WeatherDataRepository _weatherDataRepository;
    private readonly ILogger<StationProcessorWeatherCanada> _log;

    public StationProcessorWeatherCanada(
        WeatherCanadaFetcher fetcher,
        WeatherDataRepository weatherDataRepository,
        ILogger<StationProcessorWeatherCanada> log)
    {
        _fetcher = fetcher;
        _weatherDataRepository = weatherDataRepository;
        _log = log;
    }

    protected override async Task ProcessStationAsync(StationRef station, CancellationToken ct)
    {
        string json = await _fetcher.FetchLatestObservationAsync(station.Latitude, station.Longitude, ct)
            .ConfigureAwait(false);
        _log.LogDebug("Saving Weather Canada payload. station={Mli} state={State} bytes={Bytes}",
            station.Mli, station.State, json.Length);
        await _weatherDataRepository.SaveStationDataAsync(station.Mli, json, ct).ConfigureAwait(false);
        _log.LogDebug("Processed station. station={Mli} state={State}", station.Mli, station.State);
    }

    protected override async Task VerifyStationAsync(StationRef station, CancellationToken ct)
    {
        string json = await _fetcher.FetchLatestObservationAsync(station.Latitude, station.Longitude, ct)
            .ConfigureAwait(false);
        _log.LogInformation(
            "Startup Weather Canada verification fetched payload. station={Mli} state={State} bytes={Bytes}",
            station.Mli, station.State, json.Length);
    }

    protected override ILogger Logger => _log;

    protected override string Country => "CA";

    protected override string MissingSourceDescription => "Weather Canada source";
}
