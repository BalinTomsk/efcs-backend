using Microsoft.Extensions.Logging;
using WaterService.Data;

namespace WaterService.Processing;

/// <summary>
/// Runs the synchronous database steps that must happen after a station-processing cycle completes.
/// Since 11.0.0 nothing here touches SQL Server: the fish hand-over (species push, probability update) belongs to
/// env_service, which reads the cycles this service records in envdata's <c>job_run</c>.
/// </summary>
public class StationPostProcessingService
{
    private readonly WaterDataRepository _waterDataRepository;
    private readonly ILogger<StationPostProcessingService> _log;

    public StationPostProcessingService(
        WaterDataRepository waterDataRepository,
        ILogger<StationPostProcessingService> log)
    {
        _waterDataRepository = waterDataRepository;
        _log = log;
    }

    /// <summary>
    /// Records a cycle that processed at least one station (<c>sp_job_run_record</c>), so env_service knows new
    /// readings are in. <paramref name="job"/> is <c>water-ca</c>, <c>water-us</c> or <c>water</c> (both passes);
    /// status <c>ok</c> when no station failed, else <c>partial</c>.
    /// </summary>
    public virtual async Task RecordCycleAsync(
        string job, DateTime startedUtc, int succeeded, int failed, CancellationToken ct = default)
    {
        string status = failed == 0 ? "ok" : "partial";
        _log.LogInformation(
            "Recording cycle in job_run. job={Job} status={Status} succeeded={Succeeded} failed={Failed}",
            job, status, succeeded, failed);
        await _waterDataRepository.RecordCycleAsync(
                job, startedUtc, DateTime.UtcNow, status, succeeded, failed == 0 ? null : $"failed={failed}", ct)
            .ConfigureAwait(false);
    }

    /// <summary>Cleanup that must run after every cycle, even if no station was processed successfully.</summary>
    public virtual async Task CleanOldWaterDataAsync(CancellationToken ct = default)
    {
        _log.LogInformation("Running post-processing procedure {Procedure}", "sp_clean_old_water_data");
        await _waterDataRepository.CleanOldWaterDataAsync(ct).ConfigureAwait(false);
    }
}
