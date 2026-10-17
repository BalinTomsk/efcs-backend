using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Context;
using WaterService.Configuration;
using WaterService.Data;
using WaterService.Web;
using WaterService.Status;

namespace WaterService.Processing;

/// <summary>
/// Schedules and runs the recurring station-processing cycle.
///
/// <para>In normal mode the CA and US passes each run on their <strong>own</strong> schedule of the same cron
/// expression, so a slow CA pass (one request per station) cannot hold back the US refresh. Each scheduled
/// pass is followed by post-processing — species push only when at least one station succeeded, cleanup
/// always — serialised between the two countries. A pass that overruns its period delays only its own next
/// run instead of overlapping. Startup runs (<see cref="RunCycleAsync"/>) still process both countries
/// together and post-process exactly once.</para>
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
    private readonly ServiceActivity? _activity;
    private readonly CronExpression _cron;

    // The CA and US schedules each post-process after their pass; never let the two run the procs at once.
    private readonly SemaphoreSlim _postProcessingGate = new(1, 1);

    public StationWorker(
        WaterStationRepository repo,
        StationProcessorCA processorCA,
        StationProcessorUS processorUS,
        StationPostProcessingService postProcessing,
        StationHttp503BackoffService http503Backoff,
        WaterMetrics metrics,
        IOptions<WorkerOptions> options,
        ILogger<StationWorker> log,
        ServiceActivity? activity = null)
    {
        _activity = activity;
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

        // Startup verification: process specific station(s) to verify deployment health.
        // LOG-LEVEL POLICY: everything in this block that reports a successful start stays at Information
        // (failures at Warning/Error) so a deploy can be verified without enabling Debug. Per-station
        // chatter may be demoted for volume; these may not. Pinned by StationWorkerTests — see CHANGELOG.md.
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

        // Independent schedules: before this the single loop awaited both passes, so a ~28 h CA pass meant the
        // ~1 h US pass also refreshed only once per ~28 h.
        await Task.WhenAll(
            RunScheduleAsync("CA", stoppingToken),
            RunScheduleAsync("US", stoppingToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// One country's scheduler loop: wait for the next cron fire, run that country's cycle, record the
    /// outcome. A cycle that overruns delays only this country's next run.
    /// </summary>
    private async Task RunScheduleAsync(string country, CancellationToken stoppingToken)
    {
        // Yield so the two schedules start independently rather than the first running on the caller.
        await Task.Yield();
        DateTime? lastFireUtc = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Task.Delay can wake a few ms before the fire time. Never compute the next fire from a clock that
            // is still before the last one, or a short cycle would run twice for the same fire.
            DateTime nowUtc = DateTime.UtcNow;
            DateTime from = lastFireUtc is { } last && last > nowUtc ? last : nowUtc;
            DateTime? next = _cron.GetNextOccurrence(from);
            if (next is null)
            {
                _log.LogWarning("Cron expression never fires again; stopping scheduler. country={Country} cron=\"{Cron}\"",
                    country, _options.Cron);
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

            lastFireUtc = next.Value;
            DateTime startUtc = DateTime.UtcNow;
            try
            {
                await RunCountryCycleAsync(country, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Station cycle failed. country={Country}", country);
            }
            finally
            {
                RecordCycleOutcome(startUtc, DateTime.UtcNow, country, lastFireUtc);
            }
        }
    }

    /// <summary>
    /// Makes a cycle that overran its cron period observable: the scheduler silently skips the next trigger
    /// in that case, so without this a slowly degrading cycle time produces data gaps with no signal.
    /// </summary>
    /// <param name="country">The country whose scheduled cycle this was; <c>null</c> for a both-country startup cycle.</param>
    /// <param name="scheduledUtc">
    /// The cron fire this cycle ran for. The overrun check measures from it, not from <paramref name="startUtc"/>:
    /// a delay that woke a few ms early made the cycle's own fire look like the next one, so every cycle that
    /// started early was logged as an overrun.
    /// </param>
    internal void RecordCycleOutcome(DateTime startUtc, DateTime endUtc, string? country = null, DateTime? scheduledUtc = null)
    {
        long durationSeconds = (long)(endUtc - startUtc).TotalSeconds;
        DateTime? nextFire = _cron.GetNextOccurrence(scheduledUtc ?? startUtc);
        if (nextFire is not null && endUtc >= nextFire.Value)
        {
            _metrics.CycleOverrun();
            _log.LogWarning(
                "Station cycle overran its cron period; the next scheduled cycle was skipped. "
                + "country={Country} durationSeconds={Duration} cron=\"{Cron}\"",
                LogValue(country, "<all>"), durationSeconds, _options.Cron);
        }
        else
        {
            _log.LogInformation("Station cycle duration. country={Country} durationSeconds={Duration}",
                LogValue(country, "<all>"), durationSeconds);
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
            Exception? postProcessingFailure = await PostProcessAsync(succeeded, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Runs one country's scheduled cycle: its pass, then post-processing (species push only when at least
    /// one station succeeded; cleanup always).
    /// </summary>
    /// <returns>The number of stations processed successfully.</returns>
    public async Task<int> RunCountryCycleAsync(string country, CancellationToken ct)
    {
        string correlationId = Guid.NewGuid().ToString("N").Substring(0, 8);
        PassStats stats = await AwaitPassAsync(RunPassAsync(country, null, correlationId, ct), country)
            .ConfigureAwait(false);

        using (LogContext.PushProperty(CorrelationIdProperty, correlationId))
        {
            Exception? postProcessingFailure = await PostProcessAsync(stats.Succeeded, ct).ConfigureAwait(false);

            _log.LogInformation(
                "Station cycle completed. country={Country} successfulStations={Succeeded} failedStations={Failed} "
                + "lastProcessedStation={Processed} lastFailedStation={Failed2}",
                country, stats.Succeeded, stats.Failed,
                LogValue(stats.LastProcessedStation), LogValue(stats.LastFailedStation));

            if (postProcessingFailure is not null)
            {
                throw postProcessingFailure;
            }
        }

        return stats.Succeeded;
    }

    /// <summary>
    /// Runs species post-processing (only when <paramref name="succeeded"/> &gt; 0) and then old-data cleanup,
    /// one caller at a time. A species-push failure never blocks cleanup.
    /// </summary>
    /// <returns>
    /// The failure for the caller to rethrow after its summary log (both combined if both steps failed), or
    /// <c>null</c>.
    /// </returns>
    private async Task<Exception?> PostProcessAsync(int succeeded, CancellationToken ct)
    {
        await _postProcessingGate.WaitAsync(ct).ConfigureAwait(false);
        try
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

            return postProcessingFailure;
        }
        finally
        {
            _postProcessingGate.Release();
        }
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

        // US stations are read from one USGS snapshot per pass rather than one request per station.
        if (country == "US")
        {
            await _processorUS.PrepareCycleAsync(stations.Select(s => s.Mli).ToList(), requestedMli, ct)
                .ConfigureAwait(false);
        }

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

            // Pause BETWEEN stations only — sleeping after the final station just delays the cycle. US
            // stations make no request of their own (they come from the snapshot), so they never pause.
            if (anyProcessed && country != "US" && _options.PauseBetweenStationsMs > 0)
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
            _metrics.StationProcessed(country, outcome);
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

                // The feed itself is down, so every remaining station would fail the same way. Abandon
                // the pass and let the next cycle retry once the breaker has had time to close.
                if (outcome == ProcessingOutcome.FailedUpstreamOpen)
                {
                    _log.LogWarning(
                        "Stopping station pass because upstream feed is unavailable. country={Country} station={Mli} state={State}",
                        country, station.Mli, station.State);
                    break;
                }
            }

            _log.LogDebug("Processed station. country={Country} station={Mli} state={State}",
                country, station.Mli, station.State);
        }

        return new PassStats(country, succeeded, failed, lastProcessedStation, lastFailedStation);
    }

    private Task<ProcessingOutcome> ProcessStationAsync(string country, string mli, string state, int tz, CancellationToken ct)
    {
        _activity?.Record("station " + mli);
        return country switch
        {
            "CA" => _processorCA.ProcessWithOutcomeAsync(mli, state, tz, ct),
            "US" => _processorUS.ProcessWithOutcomeAsync(mli, state, tz, ct),
            _ => throw new ArgumentException("Unsupported country " + country, nameof(country)),
        };
    }

    private static string ProviderFor(string country) =>
        country switch
        {
            "CA" => ProviderCA,
            "US" => ProviderUS,
            _ => throw new ArgumentException("Unsupported country " + country, nameof(country)),
        };

    private static string LogValue(string? value, string missing = "<none>") => value ?? missing;

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
