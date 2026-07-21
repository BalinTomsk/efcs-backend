using System.Net;
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
    public async Task<bool> ProcessAsync(string mli, string state, int tz, CancellationToken ct) =>
        await ProcessWithOutcomeAsync(mli, state, tz, ct).ConfigureAwait(false) == ProcessingOutcome.Processed;

    /// <summary>
    /// Processes a single station and returns the handled outcome.
    /// </summary>
    public async Task<ProcessingOutcome> ProcessWithOutcomeAsync(string mli, string state, int tz, CancellationToken ct)
    {
        try
        {
            await ProcessStationAsync(mli, state, tz, ct).ConfigureAwait(false);
            return ProcessingOutcome.Processed;
        }
        catch (Exception ex)
        {
            return HandleProcessingException(mli, state, ex);
        }
    }

    protected abstract Task ProcessStationAsync(string mli, string state, int tz, CancellationToken ct);

    protected abstract ILogger Logger { get; }

    protected abstract string Country { get; }

    protected abstract string MissingSourceDescription { get; }

    private ProcessingOutcome HandleProcessingException(string mli, string state, Exception ex)
    {
        if (ex is FileNotFoundException)
        {
            Logger.LogDebug(
                "Skipping {StationLabel} with no published {MissingSource}. station={Mli} state={State}",
                StationLabel, MissingSourceDescription, mli, state);
            return ProcessingOutcome.Skipped;
        }

        // Concise: type + innermost message, NOT the full stack. Most failures here are transient network
        // / parse issues on one station (the other service + next cycle cover it), so a multi-KB stack per
        // failed station is noise. Raise this to a stack-carrying log only when diagnosing a specific bug.
        Logger.LogWarning(
            "{StationLabel} processing failed. station={Mli} state={State} error={Error}",
            StationLabel, mli, state, Summarize(ex));
        return IsHttp503(ex) ? ProcessingOutcome.FailedHttp503 : ProcessingOutcome.Failed;
    }

    private string StationLabel => Country + " station";

    /// <summary>Renders an exception as <c>Type: innermost message</c> for a compact one-line log.</summary>
    private static string Summarize(Exception ex)
    {
        Exception innermost = ex;
        while (innermost.InnerException is not null)
        {
            innermost = innermost.InnerException;
        }
        return innermost == ex
            ? $"{ex.GetType().Name}: {ex.Message}"
            : $"{ex.GetType().Name}: {ex.Message} -> {innermost.GetType().Name}: {innermost.Message}";
    }

    private static bool IsHttp503(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable })
        {
            return true;
        }

        string? message = ex.Message;
        return message is not null && message.Contains("HTTP 503", StringComparison.OrdinalIgnoreCase);
    }
}
