using Microsoft.Extensions.Logging;
using WeatherService.Data;

namespace WeatherService.Processing;

/// <summary>
/// Runs the database steps that must happen, in order, after a healthy station-processing cycle: record the cycle in
/// envdata's <c>job_run</c>, then the weather retention.
///
/// <para>Since 11.0.0 nothing here touches SQL Server. The fish hand-over this service used to run itself
/// (<c>spPushSpeciesFromLakeToStation</c>, <c>spTotalUpdateProbability</c>) belongs to env_service, which reads the
/// cycles recorded here. During the shadow period the Java weather service keeps running both on SQL Server.</para>
///
/// <para>Serialised with a semaphore: the provider workers finish their cycles independently, and the retention
/// deletes shared rows. (The Java service used a <c>synchronized</c> method for the same reason.)</para>
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

    /// <summary>
    /// Records the cycle (<c>sp_job_run_record</c>; job <c>{provider}-{country}</c>, e.g. <c>open-ca</c>; only when at
    /// least one station was saved, so env_service hands over only cycles that brought weather), then runs
    /// <c>sp_clean_old_weather_data</c>. A failure stops the run and surfaces to the caller.
    /// </summary>
    public virtual async Task RunAfterStationProcessingAsync(
        string provider, string country, DateTime startedUtc, int succeeded, int failed, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (succeeded > 0)
            {
                string job = JobName(provider, country);
                string status = failed == 0 ? "ok" : "partial";
                _log.LogInformation(
                    "Recording cycle in job_run. job={Job} status={Status} succeeded={Succeeded} failed={Failed}",
                    job, status, succeeded, failed);
                await _weatherDataRepository.RecordCycleAsync(
                        job, startedUtc, DateTime.UtcNow, status, succeeded, failed == 0 ? null : $"failed={failed}", ct)
                    .ConfigureAwait(false);
            }

            _log.LogInformation("Running post-processing procedure {Procedure}", "sp_clean_old_weather_data");
            await _weatherDataRepository.CleanOldWeatherDataAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary><c>job_run.job</c> of a provider/country cycle: lower case, at most 32 characters.</summary>
    internal static string JobName(string provider, string country)
    {
        string job = (provider + "-" + country).ToLowerInvariant();
        return job.Length > 32 ? job[..32] : job;
    }
}
