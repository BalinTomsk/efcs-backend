using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WeatherService.Configuration;
using WeatherService.Data;
using WeatherService.Domain;
using WeatherService.Reporting;

namespace WeatherService.Processing;

/// <summary>
/// Runs the weather-processing loops — one independent loop per provider/country pairing.
///
/// <para>Each loop does a startup verification against a known-good station, then repeats: reserve
/// today's slice of the provider's API budget, walk the stations it can afford, run post-processing if
/// the pass was healthy, record the cycle for the weekly report, and sleep until the next midnight.
/// Work inside a cycle is deliberately spread across an eight-hour budget rather than run flat out —
/// these are public/metered APIs, and pacing is what keeps the keys alive.</para>
/// </summary>
public class StationWorker : BackgroundService
{
    private const long MinDelayBetweenStationsMs = 2000L;
    private const string DefaultCountry = "US";

    private static readonly TimeSpan SummaryLogInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan CycleFailureCooldown = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Window a provider's whole daily allowance is spread across when no explicit
    /// <c>&lt;PROVIDER&gt;_TIMEOUT</c> is configured. Half a day, so a cycle that spends the full budget
    /// still finishes well inside its 24-hour slot.
    /// </summary>
    internal static readonly TimeSpan DerivedPacingWindow = TimeSpan.FromHours(12);

    private static readonly WorkerDefinition WeatherGovUs = new(
        "weather-gov", "Weather.gov", "US",
        new StationRef("KNYC", 40.7128, -74.0060, "NY"));

    private static readonly WorkerDefinition OpenMeteoCa = new(
        "open", "Open-Meteo", "CA",
        new StationRef("STARTUP-OPEN-CA", 43.6532, -79.3832, "ON"));

    private static readonly WorkerDefinition VisualCrossingUs = new(
        "visual-crossing", "Visual Crossing", "US",
        new StationRef("STARTUP-VISUAL-US", 48.3060, -120.6543, "WA"));

    private static readonly WorkerDefinition GoogleWeatherUs = new(
        "google-weather", "Google Weather", "US",
        new StationRef("STARTUP-GOOGLE-US", 48.3060, -120.6543, "WA"));

    private static readonly WorkerDefinition WeatherCanadaCa = new(
        "weather-canada", "Weather Canada", "CA",
        new StationRef("STARTUP-WEATHER-CANADA-CA", 43.6532, -79.3832, "ON"));

    private static readonly WorkerDefinition WundergroundUs = new(
        "wunderground", "Wunderground", "US",
        new StationRef("STARTUP-WUNDERGROUND-US", 48.3060, -120.6543, "WA"));

    private static readonly WorkerDefinition[] Workers =
        [WeatherGovUs, OpenMeteoCa, VisualCrossingUs, GoogleWeatherUs, WeatherCanadaCa, WundergroundUs];

    private readonly WeatherStationRepository _stationRepository;
    private readonly StationProcessorOpen _stationProcessorOpen;
    private readonly StationProcessorWeatherGov _stationProcessorWeatherGov;
    private readonly StationProcessorVisualCrossing _stationProcessorVisualCrossing;
    private readonly StationProcessorGoogleWeather _stationProcessorGoogleWeather;
    private readonly StationProcessorWeatherCanada _stationProcessorWeatherCanada;
    private readonly StationProcessorWunderground _stationProcessorWunderground;
    private readonly StationPostProcessingService _postProcessingService;
    private readonly CycleReportRecorder _cycleReportRecorder;
    private readonly WeatherApiUsageTracker _usageTracker;
    private readonly WeatherStationCoverageRepository _coverageRepository;
    private readonly WorkerOptions _options;
    private readonly ILogger<StationWorker> _log;

    public StationWorker(
        WeatherStationRepository stationRepository,
        StationProcessorOpen stationProcessorOpen,
        StationProcessorWeatherGov stationProcessorWeatherGov,
        StationProcessorVisualCrossing stationProcessorVisualCrossing,
        StationProcessorGoogleWeather stationProcessorGoogleWeather,
        StationProcessorWeatherCanada stationProcessorWeatherCanada,
        StationProcessorWunderground stationProcessorWunderground,
        StationPostProcessingService postProcessingService,
        CycleReportRecorder cycleReportRecorder,
        WeatherApiUsageTracker usageTracker,
        WeatherStationCoverageRepository coverageRepository,
        IOptions<WorkerOptions> options,
        ILogger<StationWorker> log)
    {
        _stationRepository = stationRepository;
        _stationProcessorOpen = stationProcessorOpen;
        _stationProcessorWeatherGov = stationProcessorWeatherGov;
        _stationProcessorVisualCrossing = stationProcessorVisualCrossing;
        _stationProcessorGoogleWeather = stationProcessorGoogleWeather;
        _stationProcessorWeatherCanada = stationProcessorWeatherCanada;
        _stationProcessorWunderground = stationProcessorWunderground;
        _postProcessingService = postProcessingService;
        _cycleReportRecorder = cycleReportRecorder;
        _usageTracker = usageTracker;
        _coverageRepository = coverageRepository;
        _options = options.Value;
        _log = log;
    }

    /// <summary>
    /// Cleared to make a cycle stop between stations. Separate from the cancellation token so a test can
    /// exercise the stopped-early path without cancelling everything else.
    /// </summary>
    internal bool Running { get; set; } = true;

    /// <summary>Outcome of a single <c>--console</c> pass, used to pick the process exit code.</summary>
    public readonly record struct RunResult(int ProcessedStations, int FailedStations);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = new List<Task>(Workers.Length);

        foreach (WorkerDefinition worker in Workers)
        {
            if (WorkerDisabledReason(worker.Provider, _options) is { } reason)
            {
                _log.LogWarning(
                    "Weather worker not started. provider={Provider} country={Country} reason={Reason}",
                    worker.ReportName, worker.Country, reason);
                continue;
            }

            _log.LogInformation("Started background weather worker. provider={Provider} country={Country}",
                worker.ReportName, worker.Country);
            loops.Add(Task.Run(() => RunStartupVerificationThenLoopAsync(worker, stoppingToken), stoppingToken));
        }

        if (loops.Count == 0)
        {
            _log.LogError("No weather worker could start; every provider is missing its configuration.");
            return;
        }

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    /// <summary>
    /// Explains why a provider's worker must not start, or returns <c>null</c> when it is good to go.
    /// The text is logged verbatim, so it names the environment variable an operator has to change.
    ///
    /// <para>Two independent reasons, checked in this order:</para>
    /// <list type="number">
    /// <item>The operator turned it off (<c>&lt;PROVIDER&gt;_ENABLED=false</c>). Checked first so a
    /// deliberately disabled provider does not also nag about a key it will never use.</item>
    /// <item>A metered provider has no API key. Only Visual Crossing and Google Weather have one —
    /// Weather.gov, Open-Meteo and Weather Canada are keyless public feeds. Starting such a worker
    /// anyway would fail every station it touched, pushing the cycle's failure rate past
    /// <c>MaxFailureRate</c> and suppressing post-processing for a country whose other providers were
    /// perfectly healthy.</item>
    /// </list>
    /// </summary>
    internal static string? WorkerDisabledReason(string provider, WorkerOptions options)
    {
        (bool enabled, string enableVariable, string? apiKey, string? apiKeyVariable) = provider switch
        {
            "weather-gov" => (options.Enable.WeatherGov, "WEATHER_GOV_ENABLE", null, null),
            "open" => (options.Enable.OpenMeteo, "OPEN_METEO_ENABLE", null, null),
            "visual-crossing" => (options.Enable.VisualCrossing, "VISUAL_CROSSING_ENABLE",
                options.VisualCrossingApiKey, "VISUAL_CROSSING_API_KEY"),
            "google-weather" => (options.Enable.GoogleWeather, "GOOGLE_WEATHER_ENABLE",
                options.GoogleWeatherApiKey, "GOOGLE_WEATHER_API_KEY"),
            "weather-canada" => (options.Enable.WeatherCanada, "WEATHER_CANADA_ENABLE", null, null),
            "wunderground" => (options.Enable.Wunderground, "WUNDERGROUND_ENABLE",
                options.WundergroundApiKey, "WUNDERGROUND_API_KEY"),
            _ => (true, string.Empty, null, null),
        };

        if (!enabled)
        {
            return $"{enableVariable} is false";
        }

        if (apiKeyVariable is not null && string.IsNullOrWhiteSpace(apiKey))
        {
            return $"{apiKeyVariable} is not configured";
        }

        return null;
    }

    /// <summary>Runs one cycle for the default country. Console mode only.</summary>
    public Task<RunResult> RunOnceAsync(string? requestedMli, CancellationToken ct = default) =>
        RunOnceAsync(DefaultCountry, requestedMli, ct);

    /// <summary>Runs one cycle for a country's primary provider. Console mode only.</summary>
    public virtual async Task<RunResult> RunOnceAsync(
        string country, string? requestedMli, CancellationToken ct = default)
    {
        CountryPassSummary summary = await RunCycleAsync(WorkerForCountry(country), requestedMli, ct)
            .ConfigureAwait(false);
        return new RunResult(summary.SuccessfulStations, summary.FailedStations);
    }

    private async Task<CountryPassSummary> RunCycleAsync(
        WorkerDefinition worker, string? requestedMli, CancellationToken ct)
    {
        string country = worker.Country;
        bool wholeCountry = string.IsNullOrWhiteSpace(requestedMli);

        int totalSupportedStations = await _stationRepository.CountSupportedStationsAsync(country, ct)
            .ConfigureAwait(false);
        int dailyLimit = DailyLimitFor(worker);
        int requestedStations = wholeCountry
            ? Math.Min(totalSupportedStations, dailyLimit)
            : Math.Min(1, totalSupportedStations);

        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        WeatherApiUsageTracker.UsageSnapshot budget = await _usageTracker
            .SnapshotAsync(worker.Provider, today, dailyLimit, ct).ConfigureAwait(false);

        // Budget is NOT booked here — each station is charged individually just before it is fetched,
        // so an interrupted cycle costs only what it actually used. This is only the page size.
        // A single-station request still has to load enough rows to FIND that station.
        int stationLimit = wholeCountry
            ? budget.Remaining
            : budget.Remaining > 0 ? Math.Min(totalSupportedStations, dailyLimit) : 0;

        _log.LogInformation(
            "Weather API daily budget. provider={Provider} country={Country} totalSupportedStations={Total} "
            + "dailyLimit={DailyLimit} usedToday={UsedToday} requestedForCycle={Requested} "
            + "remainingToday={Remaining} persisted={Persisted}",
            worker.ReportName, country, totalSupportedStations, budget.DailyLimit, budget.UsedToday,
            requestedStations, budget.Remaining, budget.Persisted);

        IReadOnlyList<StationRef> stations = stationLimit > 0
            ? await _stationRepository.FindSupportedStationsAsync(country, stationLimit, ct).ConfigureAwait(false)
            : [];

        _log.LogInformation(
            "Loaded supported stations. provider={Provider} country={Country} count={Count} requestedStation={Station}",
            worker.ReportName, country, stations.Count, wholeCountry ? "<all>" : requestedMli);

        int timeoutSeconds = TimeoutFor(worker);
        long targetDelayMs = CalculateDelayMs(timeoutSeconds, dailyLimit);
        _log.LogInformation(
            "Weather worker pacing. provider={Provider} country={Country} delayPerStationMs={DelayMs} "
            + "source={Source}",
            worker.ReportName, country, targetDelayMs,
            timeoutSeconds > 0 ? "TIMEOUT" : $"dailyLimit/{(long)DerivedPacingWindow.TotalHours}h");

        int processed = 0;
        int skipped = 0;
        int failed = 0;
        string? lastProcessedStation = null;
        string? lastFailedStation = null;
        long nextSummaryLogAt = CurrentTimeMillis() + (long)SummaryLogInterval.TotalMilliseconds;
        bool stoppedEarly = false;
        bool budgetExhausted = false;

        foreach (StationRef station in stations)
        {
            if (!Running || ct.IsCancellationRequested)
            {
                stoppedEarly = true;
                break;
            }
            if (!wholeCountry && !string.Equals(station.Mli, requestedMli, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Charge this one station before fetching it. A restart therefore forfeits at most the
            // station in flight, not the rest of the day's allowance.
            if (!await _usageTracker.TryConsumeAsync(worker.Provider, today, dailyLimit, ct).ConfigureAwait(false))
            {
                budgetExhausted = true;
                break;
            }

            long startedAt = CurrentTimeMillis();
            ProcessingOutcome outcome = await ProcessorFor(worker).ProcessAsync(station, country, ct)
                .ConfigureAwait(false);

            switch (outcome)
            {
                case ProcessingOutcome.Processed:
                    processed++;
                    lastProcessedStation = station.Mli;
                    break;
                case ProcessingOutcome.Skipped:
                    skipped++;
                    break;
                default:
                    failed++;
                    lastFailedStation = station.Mli;
                    break;
            }

            await RecordCoverageAsync(worker, station, outcome, ct).ConfigureAwait(false);

            long remainingDelayMs = targetDelayMs - (CurrentTimeMillis() - startedAt);
            var progress = new CountryPassSummary(
                worker.ReportName, country, processed, skipped, failed, lastProcessedStation, lastFailedStation);
            nextSummaryLogAt = await SleepUntilNextStationWithHourlySummariesAsync(
                remainingDelayMs, nextSummaryLogAt, progress, ct).ConfigureAwait(false);
        }

        var summary = new CountryPassSummary(
            worker.ReportName, country, processed, skipped, failed, lastProcessedStation, lastFailedStation);
        LogCountryPassSummary(summary);

        if (budgetExhausted)
        {
            // A normal end to the day's work, not a fault: the allowance ran out mid-pass. The stations
            // that did run are sound, so post-processing still applies.
            _log.LogInformation(
                "Daily API allowance spent; ending cycle. provider={Provider} country={Country} processed={Processed}",
                worker.ReportName, country, processed);
        }

        if (stoppedEarly)
        {
            _log.LogInformation(
                "Worker stopped before cycle completion; skipping post-processing. "
                + "processed={Processed} skipped={Skipped} failed={Failed}", processed, skipped, failed);
            return summary;
        }

        await MaybeRunPostProcessingAsync(summary, ct).ConfigureAwait(false);
        return summary;
    }

    /// <summary>
    /// Flags whether this provider could serve this gauge, so <c>fn_weather_uncovered_stations</c> can
    /// hand the gaps to a fallback worker.
    ///
    /// <para>Only PROCESSED and SKIPPED are coverage facts. A failure is transient — a timeout or a 503
    /// says nothing about whether the provider covers the point, and recording it would send a
    /// perfectly-served gauge to the fallback worker on the strength of one bad night.</para>
    ///
    /// <para>Never allowed to fail the station: the flag is an optimisation, and the payload for this
    /// cycle is already saved by the time we get here.</para>
    /// </summary>
    private async Task RecordCoverageAsync(
        WorkerDefinition worker, StationRef station, ProcessingOutcome outcome, CancellationToken ct)
    {
        if (outcome is not (ProcessingOutcome.Processed or ProcessingOutcome.Skipped))
        {
            return;
        }

        try
        {
            await _coverageRepository
                .SaveAsync(station.Mli, worker.Provider, outcome == ProcessingOutcome.Processed, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not record provider coverage. provider={Provider} station={Mli}",
                worker.ReportName, station.Mli);
        }
    }

    private async Task RunStartupVerificationThenLoopAsync(WorkerDefinition worker, CancellationToken ct)
    {
        await RunStartupVerificationAsync(worker, ct).ConfigureAwait(false);
        await LoopAsync(worker, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches one known-good station for this provider and logs the result, so a deploy gets an
    /// immediate per-provider pass/fail signal instead of waiting hours for the first real failure.
    /// Nothing is persisted, and a failure never stops the loop from starting.
    /// </summary>
    private async Task RunStartupVerificationAsync(WorkerDefinition worker, CancellationToken ct)
    {
        if (!_options.StartupVerificationEnabled || !Running || ct.IsCancellationRequested)
        {
            return;
        }

        StationRef station = worker.StartupStation;
        long startedAt = CurrentTimeMillis();
        _log.LogInformation(
            "Startup weather worker verification started. provider={Provider} country={Country} station={Mli} state={State}",
            worker.ReportName, worker.Country, station.Mli, station.State);

        ProcessingOutcome outcome = await ProcessorFor(worker)
            .VerifyStartupAsync(station, worker.Country, ct).ConfigureAwait(false);
        long elapsedMs = Math.Max(0L, CurrentTimeMillis() - startedAt);

        if (outcome == ProcessingOutcome.Processed)
        {
            _log.LogInformation(
                "Startup weather worker verification succeeded. provider={Provider} country={Country} station={Mli} "
                + "state={State} outcome={Outcome} elapsedMs={ElapsedMs}",
                worker.ReportName, worker.Country, station.Mli, station.State, outcome, elapsedMs);
            return;
        }

        _log.LogError(
            "Startup weather worker verification failed. provider={Provider} country={Country} station={Mli} "
            + "state={State} outcome={Outcome} elapsedMs={ElapsedMs}",
            worker.ReportName, worker.Country, station.Mli, station.State, outcome, elapsedMs);
    }

    private async Task LoopAsync(WorkerDefinition worker, CancellationToken ct)
    {
        while (Running && !ct.IsCancellationRequested)
        {
            try
            {
                CountryPassSummary summary = await RunCycleAsync(worker, null, ct).ConfigureAwait(false);
                _cycleReportRecorder.Record(new CycleReportEntry(
                    DateOnly.FromDateTime(DateTime.Now),
                    worker.ReportName,
                    worker.Country,
                    summary.SuccessfulStations,
                    summary.FailedStations,
                    summary.LastProcessedStation,
                    summary.LastFailedStation));

                long sleepMs = MillisUntilNextMidnight();
                DateTimeOffset nextRunAt = DateTimeOffset.Now.AddMilliseconds(sleepMs);
                _log.LogInformation(
                    "Worker cycle completed. provider={Provider} country={Country} successfulStations={Successful} "
                    + "failedStations={Failed} lastProcessedStation={LastProcessed} lastFailedStation={LastFailed} "
                    + "nextRunAt={NextRunAt} sleepMs={SleepMs}",
                    worker.ReportName, worker.Country, summary.SuccessfulStations, summary.FailedStations,
                    LogStation(summary.LastProcessedStation), LogStation(summary.LastFailedStation),
                    nextRunAt, sleepMs);

                if (sleepMs <= 0)
                {
                    continue;
                }

                await SleepAsync(sleepMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.LogInformation("Weather worker cancelled. provider={Provider} country={Country}",
                    worker.ReportName, worker.Country);
                return;
            }
            catch (Exception ex)
            {
                // One bad cycle must not kill the loop: log it and try again. This is the last line of
                // defence — everything below already handles its own expected failures.
                _log.LogError(ex, "Weather worker loop failed. provider={Provider} country={Country} "
                    + "retryInSeconds={RetryInSeconds}",
                    worker.ReportName, worker.Country, (int)CycleFailureCooldown.TotalSeconds);

                // DELIBERATE DEVIATION from the Java service, which loops straight back and re-runs the
                // cycle. Every failure here happens before the first station (the station COUNT query), so
                // with the database down there is nothing to slow the loop: it spins at thousands of
                // iterations a second across five workers, pinning a core and burying the log. Cooling off
                // costs nothing — the next cycle is a whole day away anyway.
                await SleepAsync((long)CycleFailureCooldown.TotalMilliseconds, ct).ConfigureAwait(false);
            }
        }

        _log.LogInformation("Weather worker loop exited. provider={Provider} country={Country}",
            worker.ReportName, worker.Country);
    }

    private void LogCountryPassSummary(CountryPassSummary summary) => _log.LogInformation(
        "Country pass completed. country={Country} successfulStations={Successful} failedStations={Failed} "
        + "lastProcessedStation={LastProcessed} lastFailedStation={LastFailed} provider={Provider}",
        summary.Country, summary.SuccessfulStations, summary.FailedStations,
        LogStation(summary.LastProcessedStation), LogStation(summary.LastFailedStation), summary.Provider);

    private void LogCountryPassProgress(CountryPassSummary summary) => _log.LogInformation(
        "Country pass hourly progress. country={Country} successfulStations={Successful} failedStations={Failed} "
        + "lastProcessedStation={LastProcessed} lastFailedStation={LastFailed} provider={Provider}",
        summary.Country, summary.SuccessfulStations, summary.FailedStations,
        LogStation(summary.LastProcessedStation), LogStation(summary.LastFailedStation), summary.Provider);

    /// <summary>
    /// Waits out the pacing delay before the next station, emitting a progress line every hour so a
    /// long, slow pass is still visibly alive in the logs.
    /// </summary>
    /// <returns>The updated "next summary due at" timestamp.</returns>
    private async Task<long> SleepUntilNextStationWithHourlySummariesAsync(
        long remainingDelayMs, long nextSummaryLogAt, CountryPassSummary summary, CancellationToken ct)
    {
        long delayLeftMs = Math.Max(0L, remainingDelayMs);

        while (delayLeftMs > 0 || CurrentTimeMillis() >= nextSummaryLogAt)
        {
            long sleepMs = Math.Min(delayLeftMs, Math.Max(0L, nextSummaryLogAt - CurrentTimeMillis()));
            if (sleepMs > 0)
            {
                await SleepAsync(sleepMs, ct).ConfigureAwait(false);
                delayLeftMs -= sleepMs;
            }

            if (CurrentTimeMillis() >= nextSummaryLogAt)
            {
                LogCountryPassProgress(summary);
                do
                {
                    nextSummaryLogAt += (long)SummaryLogInterval.TotalMilliseconds;
                }
                while (CurrentTimeMillis() >= nextSummaryLogAt);
            }
        }

        return nextSummaryLogAt;
    }

    private static string LogStation(string? station) =>
        string.IsNullOrWhiteSpace(station) ? "<none>" : station;

    /// <summary>
    /// Runs post-processing only when the cycle was healthy. A high failure rate (an open circuit, mass
    /// SQL failures) is a cycle-level problem: skip post-processing so probabilities are not recomputed
    /// from partial data, and log an error for alerting. Skipped stations (no published feed) are normal
    /// and never block post-processing.
    /// </summary>
    private async Task MaybeRunPostProcessingAsync(CountryPassSummary summary, CancellationToken ct)
    {
        int processed = summary.SuccessfulStations;
        int skipped = summary.SkippedStations;
        int failed = summary.FailedStations;
        int attempted = processed + failed;
        double failureRate = attempted == 0 ? 0.0 : (double)failed / attempted;
        double threshold = _options.PostProcessing.MaxFailureRate;

        if (attempted > 0 && failureRate > threshold)
        {
            _log.LogError(
                "Cycle degraded; skipping post-processing. country={Country} processed={Processed} "
                + "skipped={Skipped} failed={Failed} failureRate={FailureRate} threshold={Threshold}",
                summary.Country, processed, skipped, failed,
                failureRate.ToString("F2", CultureInfo.InvariantCulture), threshold);
            return;
        }

        _log.LogInformation(
            "Cycle healthy; running post-processing. country={Country} processed={Processed} skipped={Skipped} "
            + "failed={Failed}", summary.Country, processed, skipped, failed);

        try
        {
            await _postProcessingService.RunAfterStationProcessingAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex,
                "Post-processing failed after healthy cycle. country={Country} processed={Processed} "
                + "skipped={Skipped} failed={Failed}", summary.Country, processed, skipped, failed);
        }
    }

    /// <summary>Pause between stations / between cycles. Overridable so tests run without real waits.</summary>
    protected virtual Task SleepAsync(long ms, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromMilliseconds(ms), ct);

    protected virtual long CurrentTimeMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Seconds between calls to one provider, in milliseconds.
    ///
    /// <para>An explicit <c>&lt;PROVIDER&gt;_TIMEOUT</c> is honoured verbatim — an operator asking for a
    /// one-second gap gets one. Otherwise it is derived by spreading the provider's daily allowance over
    /// <see cref="DerivedPacingWindow"/>, floored at <see cref="MinDelayBetweenStationsMs"/> so a
    /// nonsensically large limit cannot turn the cycle into a burst.</para>
    /// </summary>
    internal static long CalculateDelayMs(int timeoutSeconds, int dailyLimit)
    {
        if (timeoutSeconds > 0)
        {
            return timeoutSeconds * 1000L;
        }

        if (dailyLimit <= 0)
        {
            return MinDelayBetweenStationsMs;
        }

        return Math.Max((long)DerivedPacingWindow.TotalMilliseconds / dailyLimit, MinDelayBetweenStationsMs);
    }

    internal long MillisUntilNextMidnight()
    {
        DateTime now = DateTime.Now;
        DateTime nextMidnight = now.Date.AddDays(1);
        return Math.Max(0L, (long)(nextMidnight - now).TotalMilliseconds);
    }

    private static WorkerDefinition WorkerForCountry(string country) =>
        string.Equals(country, "US", StringComparison.OrdinalIgnoreCase) ? WeatherGovUs : OpenMeteoCa;

    private StationProcessorBase ProcessorFor(WorkerDefinition worker) => worker.Provider switch
    {
        "weather-gov" => _stationProcessorWeatherGov,
        "visual-crossing" => _stationProcessorVisualCrossing,
        "google-weather" => _stationProcessorGoogleWeather,
        "weather-canada" => _stationProcessorWeatherCanada,
        "wunderground" => _stationProcessorWunderground,
        _ => _stationProcessorOpen,
    };

    private int DailyLimitFor(WorkerDefinition worker) => worker.Provider switch
    {
        "weather-gov" => _options.DailyLimit.WeatherGov,
        "visual-crossing" => _options.DailyLimit.VisualCrossing,
        "google-weather" => _options.DailyLimit.GoogleWeather,
        "weather-canada" => _options.DailyLimit.WeatherCanada,
        "wunderground" => _options.DailyLimit.Wunderground,
        _ => _options.DailyLimit.OpenMeteo,
    };

    /// <summary>Configured seconds between calls to this provider; 0 means "derive from the daily limit".</summary>
    private int TimeoutFor(WorkerDefinition worker) => worker.Provider switch
    {
        "weather-gov" => _options.Timeout.WeatherGov,
        "visual-crossing" => _options.Timeout.VisualCrossing,
        "google-weather" => _options.Timeout.GoogleWeather,
        "weather-canada" => _options.Timeout.WeatherCanada,
        "wunderground" => _options.Timeout.Wunderground,
        _ => _options.Timeout.OpenMeteo,
    };

    private sealed record CountryPassSummary(
        string Provider,
        string Country,
        int SuccessfulStations,
        int SkippedStations,
        int FailedStations,
        string? LastProcessedStation,
        string? LastFailedStation);

    /// <summary>One provider/country pairing: which processor runs, for whom, and its smoke-test station.</summary>
    private sealed record WorkerDefinition(
        string Provider, string ReportName, string Country, StationRef StartupStation);
}
