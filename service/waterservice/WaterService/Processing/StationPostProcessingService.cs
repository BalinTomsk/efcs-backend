using Microsoft.Extensions.Logging;
using WaterService.Data;

namespace WaterService.Processing;

/// <summary>
/// Runs the synchronous stored procedures that must happen after a station-processing cycle completes.
/// </summary>
public sealed class StationPostProcessingService
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

    /// <summary>Pushes lake species associations down to stations after a successful cycle.</summary>
    public async Task RunAfterStationProcessingAsync(CancellationToken ct = default)
    {
        _log.LogInformation("Running post-processing procedure {Procedure}", "spPushSpeciesFromLakeToStation");
        await _waterDataRepository.PushSpeciesFromLakeToStationAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Cleanup that must run after every cycle, even if no station was processed successfully.</summary>
    public async Task CleanOldWaterDataAsync(CancellationToken ct = default)
    {
        _log.LogInformation("Running post-processing procedure {Procedure}", "sp_clean_old_water_data");
        await _waterDataRepository.CleanOldWaterDataAsync(ct).ConfigureAwait(false);
    }
}
