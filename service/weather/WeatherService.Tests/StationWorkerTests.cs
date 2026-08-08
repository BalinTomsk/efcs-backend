using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Core;
using WeatherService.Configuration;
using WeatherService.Data;
using WeatherService.Domain;
using WeatherService.Processing;
using WeatherService.Reporting;
using static WeatherService.Tests.TestSupport;

namespace WeatherService.Tests;

/// <summary>
/// Covers one cycle end to end with the network, database and clock replaced: how many stations get
/// attempted, how outcomes are counted, and whether post-processing is allowed to run afterwards.
/// </summary>
public class StationWorkerTests
{
    private const long TwelveHoursMs = 12L * 60 * 60 * 1000;

    private static readonly StationRef[] ThreeStations =
    [
        new("MLI-1", 1, 1, "WA"),
        new("MLI-2", 2, 2, "OR"),
        new("MLI-3", 3, 3, "CA"),
    ];

    [Test]
    public async Task ExplicitTimeout_IsUsedVerbatim()
    {
        // An operator asking for a 5-second gap gets exactly that, floor or no floor.
        await Assert.That(StationWorker.CalculateDelayMs(timeoutSeconds: 5, dailyLimit: 1400)).IsEqualTo(5000L);
        await Assert.That(StationWorker.CalculateDelayMs(timeoutSeconds: 1, dailyLimit: 1400)).IsEqualTo(1000L);
    }

    [Test]
    public async Task NoTimeout_DerivesTheGapFromTheDailyLimit()
    {
        // The whole daily allowance spread over 12 hours, so a full-budget cycle still fits in its day.
        await Assert.That(StationWorker.CalculateDelayMs(timeoutSeconds: 0, dailyLimit: 1400))
            .IsEqualTo(TwelveHoursMs / 1400);
        await Assert.That(StationWorker.CalculateDelayMs(timeoutSeconds: 0, dailyLimit: 161))
            .IsEqualTo(TwelveHoursMs / 161);
    }

    [Test]
    public async Task DerivedGap_NeverDropsBelowTheFloor()
    {
        // A nonsensically large limit must not turn the cycle into a burst.
        await Assert.That(StationWorker.CalculateDelayMs(timeoutSeconds: 0, dailyLimit: 1_000_000))
            .IsEqualTo(2000L);
    }

    [Test]
    public async Task NoTimeoutAndNoDailyLimit_FallsBackToTheFloor()
    {
        await Assert.That(StationWorker.CalculateDelayMs(timeoutSeconds: 0, dailyLimit: 0)).IsEqualTo(2000L);
        await Assert.That(StationWorker.CalculateDelayMs(timeoutSeconds: 0, dailyLimit: -5)).IsEqualTo(2000L);
    }

    [Test]
    public async Task NextCycleIsScheduledWithinADay()
    {
        long ms = new Harness().Worker.MillisUntilNextMidnight();

        await Assert.That(ms).IsGreaterThanOrEqualTo(0L);
        await Assert.That(ms).IsLessThanOrEqualTo(24L * 60 * 60 * 1000);
    }

    [Test]
    public async Task HealthyCycle_ProcessesEveryStationThenPostProcesses()
    {
        var harness = new Harness { Stations = ThreeStations, Outcome = _ => ProcessingOutcome.Processed };

        StationWorker.RunResult result = await harness.Worker.RunOnceAsync(null);

        await Assert.That(result.ProcessedStations).IsEqualTo(3);
        await Assert.That(result.FailedStations).IsZero();
        await Assert.That(harness.ProcessedStations).IsEquivalentTo(new[] { "MLI-1", "MLI-2", "MLI-3" });
        await Assert.That(harness.PostProcessingRuns).IsEqualTo(1);
        // Three stations at the configured 5s gap: the pacing is really spent, one wait per station.
        await Assert.That(harness.RecordedSleeps.Sum()).IsEqualTo(3 * 5000L);
    }

    [Test]
    public async Task SingleStationRequest_ProcessesOnlyThatStation()
    {
        var harness = new Harness { Stations = ThreeStations, Outcome = _ => ProcessingOutcome.Processed };

        StationWorker.RunResult result = await harness.Worker.RunOnceAsync("MLI-2");

        await Assert.That(result.ProcessedStations).IsEqualTo(1);
        await Assert.That(harness.ProcessedStations).IsEquivalentTo(new[] { "MLI-2" });
        await Assert.That(harness.PostProcessingRuns).IsEqualTo(1);
    }

    [Test]
    public async Task SkippedStations_DoNotBlockPostProcessing()
    {
        // Most stations have no feed with most providers; that is normal, not a degraded cycle.
        var harness = new Harness { Stations = ThreeStations, Outcome = _ => ProcessingOutcome.Skipped };

        StationWorker.RunResult result = await harness.Worker.RunOnceAsync(null);

        await Assert.That(result.ProcessedStations).IsZero();
        await Assert.That(harness.PostProcessingRuns).IsEqualTo(1);
    }

    [Test]
    public async Task DegradedCycle_SkipsPostProcessing()
    {
        // Recomputing probabilities from a mostly-failed pass would publish worse data than doing nothing.
        var harness = new Harness { Stations = ThreeStations, Outcome = _ => ProcessingOutcome.Failed };

        StationWorker.RunResult result = await harness.Worker.RunOnceAsync(null);

        await Assert.That(result.ProcessedStations).IsZero();
        await Assert.That(result.FailedStations).IsEqualTo(3);
        await Assert.That(harness.PostProcessingRuns).IsZero();
    }

    [Test]
    public async Task PartialFailureUnderTheThreshold_StillPostProcesses()
    {
        var harness = new Harness
        {
            Stations = ThreeStations,
            Outcome = station => station.Mli == "MLI-2" ? ProcessingOutcome.Failed : ProcessingOutcome.Processed,
        };

        StationWorker.RunResult result = await harness.Worker.RunOnceAsync(null);

        await Assert.That(result.ProcessedStations).IsEqualTo(2);
        await Assert.That(result.FailedStations).IsEqualTo(1);
        await Assert.That(harness.PostProcessingRuns).IsEqualTo(1);
    }

    [Test]
    public async Task StoppingMidCycle_SkipsProcessingAndPostProcessing()
    {
        var harness = new Harness { Stations = ThreeStations };
        harness.Worker.Running = false;

        StationWorker.RunResult result = await harness.Worker.RunOnceAsync(null);

        await Assert.That(result.ProcessedStations).IsZero();
        await Assert.That(harness.ProcessedStations).IsEmpty();
        await Assert.That(harness.PostProcessingRuns).IsZero();
    }

    [Test]
    public async Task PostProcessingFailure_IsSwallowedSoTheCycleStillReports()
    {
        var harness = new Harness
        {
            Stations = ThreeStations,
            Outcome = _ => ProcessingOutcome.Processed,
            PostProcessingError = new InvalidOperationException("procedure blew up"),
        };

        StationWorker.RunResult result = await harness.Worker.RunOnceAsync(null);

        await Assert.That(result.ProcessedStations).IsEqualTo(3);
    }

    [Test]
    public async Task ExhaustedDailyBudget_LoadsNoStationsAtAll()
    {
        // The reservation is what protects the paid quota, so a zero reservation must short-circuit the
        // pass before a single request is made.
        var harness = new Harness { Stations = ThreeStations, ReserveNothing = true };

        StationWorker.RunResult result = await harness.Worker.RunOnceAsync(null);

        await Assert.That(result.ProcessedStations).IsZero();
        await Assert.That(harness.StationsRequested).IsZero();
        await Assert.That(harness.ProcessedStations).IsEmpty();
    }

    [Test]
    public async Task CoverageIsFlaggedPerStation_ForTheFallbackWorker()
    {
        // A skip is a coverage fact ("this provider has nothing for this point"); a failure is not,
        // so a bad night must never route a well-served gauge to the fallback worker.
        var harness = new Harness
        {
            Stations = ThreeStations,
            Outcome = station => station.Mli switch
            {
                "MLI-1" => ProcessingOutcome.Processed,
                "MLI-2" => ProcessingOutcome.Skipped,
                _ => ProcessingOutcome.Failed,
            },
        };

        await harness.Worker.RunOnceAsync(null);

        await Assert.That(harness.Coverage).IsEquivalentTo(new[]
        {
            ("MLI-1", "weather-gov", true),
            ("MLI-2", "weather-gov", false),
        });
    }

    [Test]
    public async Task BudgetIsChargedPerStation_NotBookedUpFront()
    {
        // The defect this replaced booked the whole daily limit at cycle start, so a restart forfeited
        // everything the interrupted cycle had not used. Charging per station keeps the two in step.
        var harness = new Harness { Stations = ThreeStations, Outcome = _ => ProcessingOutcome.Processed };

        await harness.Worker.RunOnceAsync(null);

        await Assert.That(harness.Consumed).IsEqualTo(3);
    }

    [Test]
    public async Task ExhaustedBudgetMidCycle_StopsThePassButStillPostProcesses()
    {
        // Running out of allowance is a normal end to the day's work, not a fault: the stations that did
        // run are sound, so their data must still be pushed through post-processing.
        var harness = new Harness
        {
            Stations = ThreeStations,
            Outcome = _ => ProcessingOutcome.Processed,
            ConsumeLimit = 2,
        };

        StationWorker.RunResult result = await harness.Worker.RunOnceAsync(null);

        await Assert.That(result.ProcessedStations).IsEqualTo(2);
        await Assert.That(harness.ProcessedStations).IsEquivalentTo(new[] { "MLI-1", "MLI-2" });
        await Assert.That(harness.PostProcessingRuns).IsEqualTo(1);
    }

    [Test]
    public async Task MeteredProvidersWithoutAKey_AreNotStarted()
    {
        // Running them anyway would fail every station they touched, dragging the cycle's failure rate
        // over the threshold and suppressing post-processing for a country whose other providers were fine.
        var options = new WorkerOptions();

        await Assert.That(StationWorker.WorkerDisabledReason("visual-crossing", options))
            .IsEqualTo("VISUAL_CROSSING_API_KEY is not configured");
        await Assert.That(StationWorker.WorkerDisabledReason("google-weather", options))
            .IsEqualTo("GOOGLE_WEATHER_API_KEY is not configured");
    }

    [Test]
    public async Task KeylessPublicFeeds_StartByDefault()
    {
        // Weather.gov, Open-Meteo and Weather Canada need no credentials, so only the toggle may gate them.
        var options = new WorkerOptions();

        await Assert.That(StationWorker.WorkerDisabledReason("weather-gov", options)).IsNull();
        await Assert.That(StationWorker.WorkerDisabledReason("open", options)).IsNull();
        await Assert.That(StationWorker.WorkerDisabledReason("weather-canada", options)).IsNull();
    }

    [Test]
    public async Task MeteredProvidersWithAKey_Start()
    {
        var options = new WorkerOptions
        {
            VisualCrossingApiKey = "vc-key",
            GoogleWeatherApiKey = "gw-key",
        };

        await Assert.That(StationWorker.WorkerDisabledReason("visual-crossing", options)).IsNull();
        await Assert.That(StationWorker.WorkerDisabledReason("google-weather", options)).IsNull();
    }

    [Test]
    public async Task WhitespaceOnlyKey_CountsAsMissing()
    {
        // A key left as "   " in the env file is a typo, not a credential.
        var options = new WorkerOptions { VisualCrossingApiKey = "   " };

        await Assert.That(StationWorker.WorkerDisabledReason("visual-crossing", options))
            .IsEqualTo("VISUAL_CROSSING_API_KEY is not configured");
    }

    [Test]
    public async Task EveryProviderCanBeTurnedOffIndividually()
    {
        // The operational escape hatch: take one provider out of rotation without redeploying.
        await AssertDisabledByToggle("weather-gov", "WEATHER_GOV_ENABLE", o => o.Enable.WeatherGov = false);
        await AssertDisabledByToggle("open", "OPEN_METEO_ENABLE", o => o.Enable.OpenMeteo = false);
        await AssertDisabledByToggle("visual-crossing", "VISUAL_CROSSING_ENABLE", o => o.Enable.VisualCrossing = false);
        await AssertDisabledByToggle("google-weather", "GOOGLE_WEATHER_ENABLE", o => o.Enable.GoogleWeather = false);
        await AssertDisabledByToggle("weather-canada", "WEATHER_CANADA_ENABLE", o => o.Enable.WeatherCanada = false);
    }

    [Test]
    public async Task DisablingOneProvider_LeavesTheOthersAlone()
    {
        var options = new WorkerOptions();
        options.Enable.WeatherCanada = false;

        await Assert.That(StationWorker.WorkerDisabledReason("weather-canada", options)).IsNotNull();
        await Assert.That(StationWorker.WorkerDisabledReason("weather-gov", options)).IsNull();
        await Assert.That(StationWorker.WorkerDisabledReason("open", options)).IsNull();
    }

    [Test]
    public async Task DisabledTakesPrecedenceOverAMissingKey()
    {
        // A deliberately switched-off provider should not also nag about a key it will never use.
        var options = new WorkerOptions { VisualCrossingApiKey = string.Empty };
        options.Enable.VisualCrossing = false;

        await Assert.That(StationWorker.WorkerDisabledReason("visual-crossing", options))
            .IsEqualTo("VISUAL_CROSSING_ENABLE is false");
    }

    [Test]
    public async Task AKeyedProviderStaysOff_UntilBothToggleAndKeyAreSet()
    {
        var options = new WorkerOptions { GoogleWeatherApiKey = "gw-key" };
        options.Enable.GoogleWeather = false;

        await Assert.That(StationWorker.WorkerDisabledReason("google-weather", options))
            .IsEqualTo("GOOGLE_WEATHER_ENABLE is false");

        options.Enable.GoogleWeather = true;
        await Assert.That(StationWorker.WorkerDisabledReason("google-weather", options)).IsNull();
    }

    private static async Task AssertDisabledByToggle(string provider, string variable, Action<WorkerOptions> disable)
    {
        var options = new WorkerOptions
        {
            VisualCrossingApiKey = "vc-key",
            GoogleWeatherApiKey = "gw-key",
        };
        disable(options);

        await Assert.That(StationWorker.WorkerDisabledReason(provider, options)).IsEqualTo($"{variable} is false");
    }

    [Test]
    public async Task CycleLevelFailure_SurfacesToTheLoopRatherThanBeingSwallowed()
    {
        // The loop is the only place that decides what a dead database means (log it, cool off, retry);
        // swallowing it here would turn an outage into a silent stream of empty cycles.
        var harness = new Harness { StationLookupError = new InvalidOperationException("database is down") };

        await Assert.That(() => harness.Worker.RunOnceAsync(null)).Throws<InvalidOperationException>();
    }

    // --- harness -----------------------------------------------------------------------------------

    /// <summary>
    /// Wires a <see cref="StationWorker"/> to fakes for every collaborator and a virtual clock, so a
    /// cycle that really takes eight hours runs instantly and deterministically.
    /// </summary>
    private sealed class Harness
    {
        public Harness()
        {
            var repository = new FakeStationRepository(this);
            var postProcessing = new FakePostProcessing(this);
            Worker = new RecordingWorker(this, repository, postProcessing);
        }

        public RecordingWorker Worker { get; }

        public IReadOnlyList<StationRef> Stations { get; init; } = [];

        public Func<StationRef, ProcessingOutcome> Outcome { get; init; } = _ => ProcessingOutcome.Processed;

        public Exception? PostProcessingError { get; init; }

        public bool ReserveNothing { get; init; }

        public Exception? StationLookupError { get; init; }

        /// <summary>How many stations the ledger will allow before it reports the day spent.</summary>
        public int ConsumeLimit { get; init; } = int.MaxValue;

        public int Consumed { get; set; }

        public List<(string Mli, string Provider, bool Covered)> Coverage { get; } = [];

        /// <summary>Fixed 5s pacing so sleep assertions do not depend on the derived-gap arithmetic.</summary>
        public WorkerOptions Options { get; } = BuildOptions();

        private static WorkerOptions BuildOptions()
        {
            var options = new WorkerOptions();
            options.Timeout.WeatherGov = 5;
            options.Timeout.OpenMeteo = 5;
            return options;
        }

        public List<string> ProcessedStations { get; } = [];

        public List<long> RecordedSleeps { get; } = [];

        public int PostProcessingRuns { get; set; }

        public int StationsRequested { get; set; }
    }

    private sealed class RecordingWorker : StationWorker
    {
        private readonly Harness _harness;
        private long _now;

        public RecordingWorker(Harness harness, WeatherStationRepository repository, StationPostProcessingService postProcessing)
            : base(
                repository,
                new FakeProcessor(harness),
                new FakeProcessorWeatherGov(harness),
                null!,
                null!,
                null!,
                postProcessing,
                new CycleReportRecorder(),
                new FakeUsageTracker(harness),
                new FakeCoverageRepository(harness),
                Options.Create(harness.Options),
                NullLogger<StationWorker>.Instance)
        {
            _harness = harness;
        }

        protected override Task SleepAsync(long ms, CancellationToken ct)
        {
            _harness.RecordedSleeps.Add(ms);
            _now += ms;
            return Task.CompletedTask;
        }

        protected override long CurrentTimeMillis() => _now;
    }

    private sealed class FakeStationRepository(Harness harness) : WeatherStationRepository(null!)
    {
        public override Task<int> CountSupportedStationsAsync(string country, CancellationToken ct = default) =>
            harness.StationLookupError is null
                ? Task.FromResult(harness.Stations.Count)
                : Task.FromException<int>(harness.StationLookupError);

        public override Task<IReadOnlyList<StationRef>> FindSupportedStationsAsync(
            string country, int limit, CancellationToken ct = default)
        {
            harness.StationsRequested = limit;
            return Task.FromResult<IReadOnlyList<StationRef>>([.. harness.Stations.Take(limit)]);
        }
    }

    /// <summary>Stands in for the Weather.gov processor the US worker actually uses.</summary>
    private sealed class FakeProcessorWeatherGov(Harness harness)
        : StationProcessorWeatherGov(null!, null!, null!, NullLogger<StationProcessorWeatherGov>.Instance)
    {
        public override Task<ProcessingOutcome> ProcessAsync(
            StationRef station, string country, CancellationToken ct = default)
        {
            ProcessingOutcome outcome = harness.Outcome(station);
            if (outcome == ProcessingOutcome.Processed)
            {
                harness.ProcessedStations.Add(station.Mli);
            }
            return Task.FromResult(outcome);
        }
    }

    private sealed class FakeProcessor(Harness harness)
        : StationProcessorOpen(null!, null!, NullLogger<StationProcessorOpen>.Instance)
    {
        public override Task<ProcessingOutcome> ProcessAsync(
            StationRef station, string country, CancellationToken ct = default)
        {
            ProcessingOutcome outcome = harness.Outcome(station);
            if (outcome == ProcessingOutcome.Processed)
            {
                harness.ProcessedStations.Add(station.Mli);
            }
            return Task.FromResult(outcome);
        }
    }

    private sealed class FakePostProcessing(Harness harness)
        : StationPostProcessingService(null!, NullLogger<StationPostProcessingService>.Instance)
    {
        public override Task RunAfterStationProcessingAsync(CancellationToken ct = default)
        {
            harness.PostProcessingRuns++;
            return harness.PostProcessingError is null
                ? Task.CompletedTask
                : Task.FromException(harness.PostProcessingError);
        }
    }

    private sealed class FakeCoverageRepository(Harness harness)
        : WeatherStationCoverageRepository(null!, EmptyPipelines())
    {
        public override Task SaveAsync(string mli, string provider, bool covered, CancellationToken ct = default)
        {
            harness.Coverage.Add((mli, provider, covered));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUsageTracker(Harness harness) : WeatherApiUsageTracker(
        Options.Create(new LifecycleOptions()), NullLogger<WeatherApiUsageTracker>.Instance)
    {
        public override Task<UsageSnapshot> SnapshotAsync(
            string provider, DateOnly date, int dailyLimit, CancellationToken ct = default)
        {
            int remaining = harness.ReserveNothing ? 0 : dailyLimit;
            return Task.FromResult(new UsageSnapshot(0, dailyLimit, remaining, true));
        }

        public override Task<bool> TryConsumeAsync(
            string provider, DateOnly date, int dailyLimit, CancellationToken ct = default)
        {
            if (harness.ReserveNothing || harness.Consumed >= harness.ConsumeLimit)
            {
                return Task.FromResult(false);
            }
            harness.Consumed++;
            return Task.FromResult(true);
        }
    }
}
