using Microsoft.Extensions.Logging;
using WeatherService.Data;

namespace WeatherService.Processing;

/// <summary>
/// Runs the stored procedures that must happen, in order, after a station-processing cycle completes.
///
/// <para>Serialised with a semaphore: the five provider workers finish their cycles independently, and
/// these procedures rewrite shared aggregates, so two concurrent runs would fight over the same rows.
/// (The Java service used a <c>synchronized</c> method for the same reason.)</para>
/// </summary>
public class StationPostProcessingService
{
    private readonly WeatherDataRepository _weatherDataRepository;
    private readonly ILogger<StationPostProcessingService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StationPostProcessingService(
        WeatherDataRepository weatherDataRepository,
        ILogger<StationPostProcessingService> log)
    {
        _weatherDataRepository = weatherDataRepository;
        _log = log;
    }

    public virtual async Task RunAfterStationProcessingAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _log.LogInformation("Running post-processing procedure {Procedure}", "spPushSpeciesFromLakeToStation");
            await _weatherDataRepository.PushSpeciesFromLakeToStationAsync(ct).ConfigureAwait(false);

            _log.LogInformation("Running post-processing procedure {Procedure}", "spTotalUpdateProbability");
            await _weatherDataRepository.TotalUpdateProbabilityAsync(ct).ConfigureAwait(false);

            _log.LogInformation("Running post-processing procedure {Procedure}", "sp_clean_old_weather_data");
            await _weatherDataRepository.CleanOldWeatherDataAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
