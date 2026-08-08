using System.Net;
using Microsoft.Extensions.Logging;
using WeatherService.Domain;

namespace WeatherService.Processing;

/// <summary>
/// Shared processing template for station processors: run the provider-specific work and convert any
/// failure into a logged, handled outcome. A single station never propagates an exception into the
/// cycle — the worker's health decision is made from the counted outcomes instead.
/// </summary>
public abstract class StationProcessorBase
{
    /// <summary>Processes one station, attributing the log lines to this processor's own country.</summary>
    public Task<ProcessingOutcome> ProcessAsync(StationRef station, CancellationToken ct = default) =>
        ProcessAsync(station, Country, ct);

    /// <summary>
    /// Processes one station on behalf of <paramref name="country"/>, which can differ from the
    /// processor's own country when a worker is paired with a provider from the other side of the border.
    /// </summary>
    public virtual async Task<ProcessingOutcome> ProcessAsync(
        StationRef station, string country, CancellationToken ct = default)
    {
        try
        {
            await ProcessStationAsync(station, ct).ConfigureAwait(false);
            return ProcessingOutcome.Processed;
        }
        catch (Exception ex)
        {
            return HandleProcessingException(station, ex, country);
        }
    }

    /// <summary>
    /// Runs the startup smoke check against a known-good station. Fetches but does not persist, so a
    /// deploy gets a provider reachability signal without writing anything.
    /// </summary>
    public virtual async Task<ProcessingOutcome> VerifyStartupAsync(
        StationRef station, string country, CancellationToken ct = default)
    {
        try
        {
            await VerifyStationAsync(station, ct).ConfigureAwait(false);
            return ProcessingOutcome.Processed;
        }
        catch (Exception ex)
        {
            return HandleProcessingException(station, ex, country);
        }
    }

    /// <summary>Fetches this provider's payload for the station and persists it.</summary>
    protected abstract Task ProcessStationAsync(StationRef station, CancellationToken ct);

    /// <summary>Startup check. Defaults to the full processing path; override to fetch without saving.</summary>
    protected virtual Task VerifyStationAsync(StationRef station, CancellationToken ct) =>
        ProcessStationAsync(station, ct);

    protected abstract ILogger Logger { get; }

    /// <summary>Country this processor's provider serves.</summary>
    protected abstract string Country { get; }

    /// <summary>Name of the upstream feed, used in the "no published …" skip message.</summary>
    protected abstract string MissingSourceDescription { get; }

    private ProcessingOutcome HandleProcessingException(StationRef station, Exception ex, string country)
    {
        if (ex is FileNotFoundException)
        {
            Logger.LogInformation(
                "Skipping {StationLabel} with no published {MissingSource}. station={Mli} state={State}",
                StationLabel(country), MissingSourceDescription, station.Mli, station.State);
            return ProcessingOutcome.Skipped;
        }

        if (IsHttp503(ex))
        {
            Logger.LogWarning(
                "{StationLabel} processing failed with upstream HTTP 503. station={Mli} state={State} error={Error}",
                StationLabel(country), station.Mli, station.State, Summarize(ex));
            return ProcessingOutcome.FailedHttp503;
        }

        Logger.LogWarning(
            ex, "{StationLabel} processing failed. station={Mli} state={State}",
            StationLabel(country), station.Mli, station.State);
        return ProcessingOutcome.Failed;
    }

    private static string StationLabel(string country) => country + " station";

    /// <summary>Renders an exception as <c>Type: innermost message</c> for a compact one-line log.</summary>
    private static string Summarize(Exception ex)
    {
        Exception innermost = ex;
        while (innermost.InnerException is not null)
        {
            innermost = innermost.InnerException;
        }
        return ReferenceEquals(innermost, ex)
            ? $"{ex.GetType().Name}: {ex.Message}"
            : $"{ex.GetType().Name}: {ex.Message} -> {innermost.GetType().Name}: {innermost.Message}";
    }

    /// <summary>
    /// A 503 means the provider is shedding load, which is worth distinguishing from a station-specific
    /// failure. Checked structurally first, then by message, since the fetchers put the code in both.
    /// </summary>
    private static bool IsHttp503(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable })
        {
            return true;
        }

        return ex.Message.Contains("HTTP 503", StringComparison.OrdinalIgnoreCase);
    }
}
