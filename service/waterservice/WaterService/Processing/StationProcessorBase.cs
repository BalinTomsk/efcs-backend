using Microsoft.Extensions.Logging;

namespace WaterService.Processing;

/// <summary>
/// Shared processing template for station processors: run the station-specific work and convert any
/// failure into a logged, handled outcome.
/// </summary>
public abstract class StationProcessorBase
{
    /// <summary>
    /// Processes a single station.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the station was processed without error; <c>false</c> when an exception was
    /// handled (a failure, or an unpublished / skipped source).
    /// </returns>
    public async Task<bool> ProcessAsync(string mli, string state, int tz, CancellationToken ct)
    {
        try
        {
            await ProcessStationAsync(mli, state, tz, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            HandleProcessingException(mli, state, ex);
            return false;
        }
    }

    protected abstract Task ProcessStationAsync(string mli, string state, int tz, CancellationToken ct);

    protected abstract ILogger Logger { get; }

    protected abstract string Country { get; }

    protected abstract string MissingSourceDescription { get; }

    private void HandleProcessingException(string mli, string state, Exception ex)
    {
        if (ex is FileNotFoundException)
        {
            Logger.LogDebug(
                "Skipping {StationLabel} with no published {MissingSource}. station={Mli} state={State}",
                StationLabel, MissingSourceDescription, mli, state);
            return;
        }

        Logger.LogWarning(ex,
            "{StationLabel} processing failed. station={Mli} state={State}.", StationLabel, mli, state);
    }

    private string StationLabel => Country + " station";
}
