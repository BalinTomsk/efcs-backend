using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Context;
using WaterService.Configuration;
using WaterService.Data;
using WaterService.Web;

namespace WaterService.Processing;

/// <summary>
/// Schedules and runs the recurring station-processing cycle.
///
/// <para>In normal mode an hourly cycle is registered from a cron expression. Each cycle processes the CA
/// and US stations in parallel and then runs post-processing <strong>exactly once</strong> — and only when
/// at least one station was processed successfully. The single-loop design means a cycle that overruns its
/// hour simply delays the next one instead of overlapping.</para>
/// </summary>
public sealed class StationWorker : BackgroundService
{
    private const string CorrelationIdProperty = "correlationId";
    private const string StationProperty = "station";
    private const string ProviderCA = "environment-canada";
    private const string ProviderUS = "usgs";

    private readonly WaterStationRepository _repo;
    private readonly StationProcessorCA _processorCA;
    private readonly StationProcessorUS _processorUS;
    private readonly StationPostProcessingService _postProcessing;
    private readonly StationHttp503BackoffService _http503Backoff;
    private readonly WaterMetrics _metrics;
    private readonly WorkerOptions _options;
    private readonly ILogger<StationWorker> _log;
    private readonly CronExpression _cron;

    public StationWorker(
        WaterStationRepository repo,
        StationProcessorCA processorCA,
        StationProcessorUS processorUS,
        StationPostProcessingService postProcessing,
        StationHttp503BackoffService http503Backoff,
        WaterMetrics metrics,
        IOptions<WorkerOptions> options,
        ILogger<StationWorker> log)
    {
        _repo = repo;
        _processorCA = processorCA;
        _processorUS = processorUS;
        _postProcessing = postProcessing;
        _http503Backoff = http503Backoff;
        _metrics = metrics;
        _options = options.Value;
        _log = log;
        _cron = CronExpression.Parse(_options.Cron, CronFormat.IncludeSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("Station cycle scheduling is disabled.");
            return;
        }

        _log.LogInformation("Scheduled station cycle. cron=\"{Cron}\"", _options.Cron);

        // Startup verification: process specific station(s) to verify deployment health
        if (!string.IsNullOrWhiteSpace(_options.StartupVerificationStations))
        {
            string[] verificationStations = _options.StartupVerificationStations
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            _log.LogInformation(
                "StartupVerificationStations enabled — processing {Count} station(s) for deployment verification: {Stations}",
                verificationStations.Length,
                string.Join(", ", verificationStations));

            DateTime startupUtc = DateTime.UtcNow;
            int totalProcessed = 0;
            try
            {
                foreach (string mli in verificationStations)
                {
                    int processed = await RunCycleAsync(mli, stoppingToken).ConfigureAwait(false);
                    totalProcessed += processed;
                    if (processed > 0)
                    {
                        _log.LogInformation("Startup verification: station {MLI} processed successfully.", mli);
                    }
                    else
                    {
                        _log.LogWarning("Startup verification: station {MLI} was not processed (may not exist or be supported).", mli);
                    }
                }

                if (totalProcessed == 0)
                {
                    _log.LogError("Startup verification: FAILED — no stations were processed successfully. Check station MLIs and upstream feed availability.");
                }
                else
                {
                    _log.LogInformation("Startup verification: SUCCESS — {Count} station(s) processed successfully.", totalProcessed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Startup verification cycle failed.");
            }
            finally
            {
                RecordCycleOutcome(startupUtc, DateTime.UtcNow);
            }
        }
        else if (_options.RunOnStartup)
        {
            _log.LogInformation("RunOnStartup enabled — running an immediate full cycle before scheduling.");
            DateTime startupUtc = DateTime.UtcNow;
            try
            {
                await RunCycleAsync(null, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Startup cycle failed.");
            }
            finally
            {
                RecordCycleOutcome(startupUtc, DateTime.UtcNow);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime nowUtc = DateTime.UtcNow;
            DateTime? next = _cron.GetNextOccurrence(nowUtc);
            if (next is null)
            {
                _log.LogWarning("Cron expression never fires again; stopping scheduler. cron=\"{Cron}\"", _options.Cron);
                return;
            }

            TimeSpan delay = next.Value - nowUtc;
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            DateTime startUtc = DateTime.UtcNow;
            try
            {
                await RunCycleAsync(null, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Station cycle failed.");
            }
            finally
            {
                RecordCycleOutcome(startUtc, DateTime.UtcNow);
            }
        }
    }

    /// <summary>
    /// Makes a cycle that overran its cron period observable: the scheduler silently skips the next trigger
    /// in that case, so without this a slowly degrading cycle time produces data gaps with no signal.
    /// </summary>
    internal void RecordCycleOutcome(DateTime startUtc, DateTime endUtc)
    {
        long durationSeconds = (long)(endUtc - startUtc).TotalSeconds;
        DateTime? nextFire = _cron.GetNextOccurrence(startUtc);
        if (nextFire is not null && endUtc >= nextFire.Value)
        {
            _metrics.CycleOverrun();
            _log.LogWarning(
                "Station cycle overran its cron period; the next scheduled cycle was skipped. "
                + "durationSeconds={Duration} cron=\"{Cron}\"", durationSeconds, _options.Cron);
        }
        else
        {
            _log.LogInformation("Station cycle duration. durationSeconds={Duration}", durationSeconds);
        }
    }

    /// <summary>
    /// Runs one full cycle: process CA and US stations in parallel, then run post-processing exactly once.
    /// Post-processing runs only when at least one station was processed successfully.
    /// </summary>
    /// <param name="requestedMli">Optional single station to restrict processing to; <c>null</c> for all.</param>
    /// <returns>The number of stations processed successfully across both countries.</returns>
    public async Task<int> RunCycleAsync(string? requestedMli, CancellationToken ct)
    {
        string correlationId = Guid.NewGuid().ToString("N").Substring(0, 8);

        Task<PassStats> caPass = RunPassAsync("CA", requestedMli, correlationId, ct);
        Task<PassStats> usPass = RunPassAsync("US", requestedMli, correlationId, ct);

        PassStats caStats = await AwaitPassAsync(caPass, "CA").ConfigureAwait(false);
        PassStats usStats = await AwaitPassAsync(usPass, "US").ConfigureAwait(false);
        int succeeded = caStats.Succeeded + usStats.Succeeded;
        int failed = caStats.Failed + usStats.Failed;

        using (LogContext.PushProperty(CorrelationIdProperty, correlationId))
        {
            Exception? postProcessingFailure = null;
            try
            {
                if (succeeded > 0)
                {
                    await _postProcessing.RunAfterStationProcessingAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    _log.LogWarning("Skipping post-processing: no stations were processed successfully this cycle.");
                }
            }
            catch (Exception ex)
            {
                postProcessingFailure = ex;
                _log.LogError(ex, "Species post-processing failed; still running old-data cleanup.");
            }

            try
            {
                await _postProcessing.CleanOldWaterDataAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never let a cleanup failure mask the species-push failure that preceded it.
                if (postProcessingFailure is null)
                {
                    postProcessingFailure = ex;
                }
                else
                {
                    postProcessingFailure = new AggregateException(postProcessingFailure, ex);
                    _log.LogError(ex, "Old-data cleanup also failed.");
                }
            }

            _log.LogInformation(
                "Station cycle completed. successfulStations={Succeeded} failedStations={Failed} "
                + "caLastProcessedStation={CaProcessed} usLastProcessedStation={UsProcessed} "
                + "caLastFailedStation={CaFailed} usLastFailedStation={UsFailed}",
                succeeded, failed,
                LogValue(caStats.LastProcessedStation), LogValue(usStats.LastProcessedStation),
                LogValue(caStats.LastFailedStation), LogValue(usStats.LastFailedStation));

            if (postProcessingFailure is not null)
            {
                throw postProcessingFailure;
            }
        }

        return succeeded;
    }

    private async Task<PassStats> RunPassAsync(string country, string? requestedMli, string correlationId, CancellationToken ct)
    {
        // Yield so both country passes truly run in parallel rather than the first blocking the caller.
        await Task.Yield();
        using (LogContext.PushProperty(CorrelationIdProperty, correlationId))
        {
            PassStats stats = await RunOnceStatsAsync(country, requestedMli, ct).ConfigureAwait(false);
            _log.LogInformation(
                "Station pass completed. country={Country} successfulStations={Succeeded} failedStations={Failed} "
                + "lastProcessedStation={Processed} lastFailedStation={Failed2}",
                stats.Country, stats.Succeeded, stats.Failed,
                LogValue(stats.LastProcessedStation), LogValue(stats.LastFailedStation));
            return stats;
        }
    }

    private async Task<PassStats> AwaitPassAsync(Task<PassStats> pass, string country)
    {
        try
        {
            return await pass.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Station pass failed. country={Country}", country);
            return PassStats.Empty(country);
        }
    }

    /// <summary>
    /// Processes one country's stations once and returns the number processed successfully. Does not run
    /// post-processing. Honours cancellation by stopping the pass early.
    /// </summary>
    public async Task<int> RunOnceAsync(string country, string? requestedMli, CancellationToken ct) =>
        (await RunOnceStatsAsync(country, requestedMli, ct).ConfigureAwait(false)).Succeeded;

    private async Task<PassStats> RunOnceStatsAsync(string country, string? requestedMli, CancellationToken ct)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        await _http503Backoff.RefreshDueAsync(today, ct).ConfigureAwait(false);
        IReadOnlyList<Domain.StationRef> stations = await _repo.FindSupportedAsync(country, ct).ConfigureAwait(false);
        _log.LogInformation("Loaded supported stations. country={Country} count={Count} requestedStation={Requested}",
            country, stations.Count,
            string.IsNullOrWhiteSpace(requestedMli) ? "<all>" : requestedMli);

        int succeeded = 0;
        int failed = 0;
        string? lastProcessedStation = null;
        string? lastFailedStation = null;
        bool anyProcessed = false;

        foreach (Domain.StationRef station in stations)
        {
            if (!string.IsNullOrWhiteSpace(requestedMli)
                && !string.Equals(station.Mli, requestedMli, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Pause BETWEEN stations only — sleeping after the final station just delays the cycle.
            if (anyProcessed && _options.PauseBetweenStationsMs > 0)
            {
                try
                {
                    await Task.Delay(_options.PauseBetweenStationsMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _log.LogInformation("Station pass interrupted. country={Country}", country);
                    break;
                }
            }
            anyProcessed = true;

            ProcessingOutcome outcome;
            using (LogContext.PushProperty(StationProperty, station.Mli))
            {
                outcome = await ProcessStationAsync(country, station.Mli, station.State, station.Tz, ct)
                    .ConfigureAwait(false);
            }

            bool ok = outcome == ProcessingOutcome.Processed;
            _metrics.StationProcessed(country, ok);
            if (ok)
            {
                await _http503Backoff.RecordProcessedAsync(ProviderFor(country), country, station, ct)
                    .ConfigureAwait(false);
            }
            else if (outcome == ProcessingOutcome.FailedHttp503)
            {
                await _http503Backoff.RecordHttp503Async(ProviderFor(country), country, station, today, ct)
                    .ConfigureAwait(false);
            }

            lastProcessedStation = station.Mli;
            if (ok)
            {
                succeeded++;
            }
            else
            {
                failed++;
                lastFailedStation = station.Mli;
            }

            _log.LogDebug("Processed station. country={Country} station={Mli} state={State}",
                country, station.Mli, station.State);
        }

        return new PassStats(country, succeeded, failed, lastProcessedStation, lastFailedStation);
    }

    private Task<ProcessingOutcome> ProcessStationAsync(string country, string mli, string state, int tz, CancellationToken ct) =>
        country switch
        {
            "CA" => _processorCA.ProcessWithOutcomeAsync(mli, state, tz, ct),
            "US" => _processorUS.ProcessWithOutcomeAsync(mli, state, tz, ct),
            _ => throw new ArgumentException("Unsupported country " + country, nameof(country)),
        };

    private static string ProviderFor(string country) =>
        country switch
        {
            "CA" => ProviderCA,
            "US" => ProviderUS,
            _ => throw new ArgumentException("Unsupported country " + country, nameof(country)),
        };

    private static string LogValue(string? value) => value ?? "<none>";

    private readonly record struct PassStats(
        string Country,
        int Succeeded,
        int Failed,
        string? LastProcessedStation,
        string? LastFailedStation)
    {
        public static PassStats Empty(string country) => new(country, 0, 0, null, null);
    }
}
