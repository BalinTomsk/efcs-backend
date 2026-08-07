using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Core;
using WaterService.Configuration;
using WaterService.Data;
using WaterService.Domain;
using WaterService.Processing;
using WaterService.Web;

namespace WaterService.Tests;

/// <summary>
/// Covers <see cref="StationWorker"/> cycle and pass semantics — the C# counterpart of the Java
/// <c>StationWorkerTest</c>.
///
/// <para>The worker's collaborators are real types with their I/O members overridden (they are
/// constructed with <c>null!</c> internals, which is safe precisely because every method that would
/// touch a socket or a connection is replaced here). That keeps the production wiring unchanged — no
/// extra interfaces or DI indirection introduced purely for tests.</para>
/// </summary>
public class StationWorkerTests
{
    private const string EveryHour = "0 0 * * * *";

    // --- pass mechanics ----------------------------------------------------------------------------

    [Test]
    public async Task RunOnce_ProcessesFilteredStations_AndCountsSuccesses()
    {
        var harness = new Harness();
        harness.Stations["CA"] = [new("02JE025", "QC", -5), new("02HA014", "ON", -5)];

        int processed = await harness.Worker.RunOnceAsync("CA", "02HA014", CancellationToken.None);

        await Assert.That(processed).IsEqualTo(1);
        await Assert.That(harness.ProcessorCA.Seen).IsEquivalentTo(new[] { "02HA014" });
    }

    [Test]
    public async Task RunOnce_CountsOnlySuccessfulStations()
    {
        var harness = new Harness();
        harness.Stations["CA"] = [new("A", "QC", -5), new("B", "QC", -5), new("C", "QC", -5)];
        harness.ProcessorCA.Outcomes["B"] = ProcessingOutcome.Failed;
        harness.ProcessorCA.Outcomes["C"] = ProcessingOutcome.Skipped;

        await Assert.That(await harness.Worker.RunOnceAsync("CA", null, CancellationToken.None)).IsEqualTo(1);
    }

    [Test]
    public async Task RunOnce_RecordsSuccessAndFailureCounters()
    {
        var harness = new Harness();
        harness.Stations["CA"] = [new("A", "QC", -5), new("B", "QC", -5)];
        harness.ProcessorCA.Outcomes["B"] = ProcessingOutcome.Failed;

        await harness.Worker.RunOnceAsync("CA", null, CancellationToken.None);

        await Assert.That(harness.Metrics.Successes("CA")).IsEqualTo(1);
        await Assert.That(harness.Metrics.Failures("CA")).IsEqualTo(1);
    }

    [Test]
    public async Task RunOnce_SkippedStation_IsNotCountedAsAFailure()
    {
        // A skip means the upstream feed publishes nothing for that station (HTTP 404) — routine, and true
        // for most of the CA fleet. Counting it as outcome="failure" made a healthy cycle read as a
        // near-total outage (2 success / 44 "failure" with not one failure in the logs) and buried the
        // real failures. The skip gets its own label; nothing lands under "failure".
        var harness = new Harness();
        harness.Stations["CA"] = [new("A", "QC", -5), new("B", "QC", -5)];
        harness.ProcessorCA.Outcomes["B"] = ProcessingOutcome.Skipped;

        await harness.Worker.RunOnceAsync("CA", null, CancellationToken.None);

        await Assert.That(harness.Metrics.Failures("CA")).IsEqualTo(0);
        await Assert.That(harness.Metrics.Counted("CA", "skipped")).IsEqualTo(1);
        await Assert.That(harness.Metrics.Successes("CA")).IsEqualTo(1);
    }

    [Test]
    public async Task RunOnce_EachFailureKindGetsItsOwnOutcomeLabel()
    {
        // 503-backed-off and breaker-open stations are distinguishable from a plain failure, so an alert
        // can target the one that actually means something is broken here.
        var harness = new Harness();
        harness.Stations["US"] = [new("A", "NY", -5), new("B", "NY", -5), new("C", "NY", -5)];
        harness.ProcessorUS.Outcomes["A"] = ProcessingOutcome.Failed;
        harness.ProcessorUS.Outcomes["B"] = ProcessingOutcome.FailedHttp503;
        harness.ProcessorUS.Outcomes["C"] = ProcessingOutcome.FailedUpstreamOpen;

        await harness.Worker.RunOnceAsync("US", null, CancellationToken.None);

        await Assert.That(harness.Metrics.Counted("US", "failure")).IsEqualTo(1);
        await Assert.That(harness.Metrics.Counted("US", "failure_503")).IsEqualTo(1);
        await Assert.That(harness.Metrics.Counted("US", "upstream_open")).IsEqualTo(1);
    }

    [Test]
    public async Task RunOnce_DoesNotPauseAfterTheFinalStation()
    {
        // The pause belongs BETWEEN stations; sleeping after the last one just delays the whole cycle.
        // Two stations => exactly one pause, so the pass must take ~1x the pause, not ~2x.
        var harness = new Harness(o => o.PauseBetweenStationsMs = 300);
        harness.Stations["CA"] = [new("A", "QC", -5), new("B", "QC", -5)];

        var stopwatch = Stopwatch.StartNew();
        await harness.Worker.RunOnceAsync("CA", null, CancellationToken.None);
        stopwatch.Stop();

        await Assert.That(stopwatch.ElapsedMilliseconds).IsGreaterThanOrEqualTo(250);
        await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(600);
    }

    // --- HTTP 503 backoff --------------------------------------------------------------------------

    [Test]
    public async Task RunOnce_RefreshesDueBackoffBeforeLoadingStations()
    {
        var harness = new Harness();
        harness.Stations["CA"] = [new("A", "QC", -5)];

        await harness.Worker.RunOnceAsync("CA", null, CancellationToken.None);

        await Assert.That(harness.BackoffRepo.Calls.First()).IsEqualTo("refresh");
        await Assert.That(harness.BackoffRepo.Calls).Contains("load-stations-after-refresh");
    }

    [Test]
    public async Task RunOnce_RecordsHttp503Failure_AndResetsBackoffOnSuccess()
    {
        var harness = new Harness();
        harness.Stations["CA"] = [new("A", "QC", -5), new("B", "QC", -5)];
        harness.ProcessorCA.Outcomes["A"] = ProcessingOutcome.FailedHttp503;

        await harness.Worker.RunOnceAsync("CA", null, CancellationToken.None);

        await Assert.That(harness.BackoffRepo.Recorded).IsEquivalentTo(new[] { "A" });
        await Assert.That(harness.BackoffRepo.Reset).IsEquivalentTo(new[] { "B" });
    }

    // --- open upstream circuit breaker -------------------------------------------------------------

    [Test]
    public async Task RunOnce_StopsCountryPass_WhenSharedUpstreamCircuitBreakerIsOpen()
    {
        // The feed is down, not the station: every remaining station would be short-circuited the same
        // way, so the pass is abandoned rather than logging thousands of identical failures.
        var harness = new Harness();
        harness.Stations["US"] = [new("08312000", "NM", -7), new("08313000", "NY", -5), new("08314000", "NY", -5)];
        harness.ProcessorUS.Outcomes["08313000"] = ProcessingOutcome.FailedUpstreamOpen;

        int processed = await harness.Worker.RunOnceAsync("US", null, CancellationToken.None);

        await Assert.That(processed).IsEqualTo(1);
        await Assert.That(harness.ProcessorUS.Seen).IsEquivalentTo(new[] { "08312000", "08313000" });
    }

    [Test]
    public async Task RunOnce_OpenBreaker_DoesNotRecordAnHttp503Backoff()
    {
        // An open breaker is not a per-station 503; recording one would back the station off for days
        // over an outage that had nothing to do with it.
        var harness = new Harness();
        harness.Stations["US"] = [new("08313000", "NY", -5)];
        harness.ProcessorUS.Outcomes["08313000"] = ProcessingOutcome.FailedUpstreamOpen;

        await harness.Worker.RunOnceAsync("US", null, CancellationToken.None);

        await Assert.That(harness.BackoffRepo.Recorded).IsEmpty();
        await Assert.That(harness.BackoffRepo.Reset).IsEmpty();
    }

    [Test]
    public async Task RunCycle_OpenBreakerOnOneCountry_DoesNotStopTheOther()
    {
        var harness = new Harness();
        harness.Stations["US"] = [new("08312000", "NM", -7), new("08313000", "NY", -5)];
        harness.Stations["CA"] = [new("02JE025", "QC", -5), new("02HA014", "ON", -5)];
        harness.ProcessorUS.Outcomes["08312000"] = ProcessingOutcome.FailedUpstreamOpen;

        int processed = await harness.Worker.RunCycleAsync(null, CancellationToken.None);

        await Assert.That(harness.ProcessorUS.Seen).IsEquivalentTo(new[] { "08312000" });
        await Assert.That(harness.ProcessorCA.Seen).IsEquivalentTo(new[] { "02JE025", "02HA014" });
        await Assert.That(processed).IsEqualTo(2);
    }

    // --- cycle semantics ---------------------------------------------------------------------------

    [Test]
    public async Task RunCycle_ProcessesBothCountries_AndRunsPostProcessingOnce()
    {
        var harness = new Harness();
        harness.Stations["CA"] = [new("02JE025", "QC", -5)];
        harness.Stations["US"] = [new("08313000", "NY", -5)];

        int processed = await harness.Worker.RunCycleAsync(null, CancellationToken.None);

        await Assert.That(processed).IsEqualTo(2);
        await Assert.That(harness.PostProcessing.SpeciesRuns).IsEqualTo(1);
        await Assert.That(harness.PostProcessing.CleanupRuns).IsEqualTo(1);
    }

    [Test]
    public async Task RunCycle_NoStationSucceeds_SkipsSpeciesPushButStillCleansOldData()
    {
        var harness = new Harness();
        harness.Stations["CA"] = [new("A", "QC", -5)];
        harness.ProcessorCA.Outcomes["A"] = ProcessingOutcome.Failed;

        await Assert.That(await harness.Worker.RunCycleAsync(null, CancellationToken.None)).IsEqualTo(0);
        await Assert.That(harness.PostProcessing.SpeciesRuns).IsEqualTo(0);
        await Assert.That(harness.PostProcessing.CleanupRuns).IsEqualTo(1);
    }

    [Test]
    public async Task RunCycle_IsolatesAFailingCountryPassFromTheOther()
    {
        var harness = new Harness();
        harness.StationLoadFailures["CA"] = new InvalidOperationException("db down");
        harness.Stations["US"] = [new("08313000", "NY", -5)];

        int processed = await harness.Worker.RunCycleAsync(null, CancellationToken.None);

        await Assert.That(processed).IsEqualTo(1);
        await Assert.That(harness.ProcessorUS.Seen).IsEquivalentTo(new[] { "08313000" });
        await Assert.That(harness.PostProcessing.CleanupRuns).IsEqualTo(1);
    }

    [Test]
    public async Task RunCycle_StillCleansOldWaterData_WhenSpeciesPostProcessingFails()
    {
        var harness = new Harness();
        harness.Stations["CA"] = [new("A", "QC", -5)];
        harness.PostProcessing.SpeciesFailure = new InvalidOperationException("species push failed");

        var thrown = await CaptureAsync(() => harness.Worker.RunCycleAsync(null, CancellationToken.None));

        await Assert.That(thrown.Message).IsEqualTo("species push failed");
        await Assert.That(harness.PostProcessing.CleanupRuns).IsEqualTo(1);
    }

    [Test]
    public async Task RunCycle_BothPostProcessingStepsFail_PropagatesBothWithSpeciesFirst()
    {
        // A cleanup failure must never mask the species failure that preceded it.
        var harness = new Harness();
        harness.Stations["CA"] = [new("A", "QC", -5)];
        harness.PostProcessing.SpeciesFailure = new InvalidOperationException("species push failed");
        harness.PostProcessing.CleanupFailure = new InvalidOperationException("cleanup failed");

        var thrown = await CaptureAsync(() => harness.Worker.RunCycleAsync(null, CancellationToken.None));

        var aggregate = thrown as AggregateException;
        await Assert.That(aggregate).IsNotNull();
        await Assert.That(aggregate!.InnerExceptions.Select(e => e.Message))
            .IsEquivalentTo(new[] { "species push failed", "cleanup failed" });
    }

    // --- cycle overrun visibility ------------------------------------------------------------------

    [Test]
    public async Task CycleOverrunningItsCronPeriod_IsCountedAndVisible()
    {
        var harness = new Harness();
        var start = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc);

        // Hourly cron: finishing 90 minutes in means the 11:00 trigger was silently skipped.
        harness.Worker.RecordCycleOutcome(start, start.AddMinutes(90));

        await Assert.That(harness.Metrics.Overruns).IsEqualTo(1);
    }

    [Test]
    public async Task CycleFinishingWithinItsCronPeriod_DoesNotCountAnOverrun()
    {
        var harness = new Harness();
        var start = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc);

        harness.Worker.RecordCycleOutcome(start, start.AddMinutes(20));

        await Assert.That(harness.Metrics.Overruns).IsEqualTo(0);
    }

    // --- scheduling ---------------------------------------------------------------------------------

    [Test]
    public async Task DisabledWorker_SchedulesNothing()
    {
        var harness = new Harness(o => o.Enabled = false);
        harness.Stations["CA"] = [new("A", "QC", -5)];

        await RunHostedAsync(harness.Worker, TimeSpan.FromMilliseconds(250));

        await Assert.That(harness.ProcessorCA.Seen).IsEmpty();
        await Assert.That(harness.PostProcessing.CleanupRuns).IsEqualTo(0);
    }

    [Test]
    public async Task EnabledWorker_DoesNotRunACycleBeforeItsFirstCronFire()
    {
        // Default cron fires at the top of the hour: nothing must run in the meantime.
        var harness = new Harness();
        harness.Stations["CA"] = [new("A", "QC", -5)];

        await RunHostedAsync(harness.Worker, TimeSpan.FromMilliseconds(250));

        await Assert.That(harness.ProcessorCA.Seen).IsEmpty();
    }

    [Test]
    public async Task RunOnStartup_RunsOneFullCycleBeforeScheduling()
    {
        var harness = new Harness(o => o.RunOnStartup = true);
        harness.Stations["CA"] = [new("02JE025", "QC", -5)];
        harness.Stations["US"] = [new("08313000", "NY", -5)];

        await RunHostedAsync(harness.Worker, TimeSpan.FromSeconds(2));

        await Assert.That(harness.ProcessorCA.Seen).IsEquivalentTo(new[] { "02JE025" });
        await Assert.That(harness.ProcessorUS.Seen).IsEquivalentTo(new[] { "08313000" });
        await Assert.That(harness.PostProcessing.SpeciesRuns).IsEqualTo(1);
    }

    [Test]
    public async Task StartupVerification_ProcessesOnlyTheNamedStations()
    {
        var harness = new Harness(o => o.StartupVerificationStations = "02HA014, 08313000");
        harness.Stations["CA"] = [new("02JE025", "QC", -5), new("02HA014", "ON", -5)];
        harness.Stations["US"] = [new("08313000", "NY", -5), new("08314000", "NY", -5)];

        await RunHostedAsync(harness.Worker, TimeSpan.FromSeconds(2));

        await Assert.That(harness.ProcessorCA.Seen).IsEquivalentTo(new[] { "02HA014" });
        await Assert.That(harness.ProcessorUS.Seen).IsEquivalentTo(new[] { "08313000" });
    }

    [Test]
    public async Task StartupVerification_ReportsSuccessAtInformation()
    {
        // POLICY: the startup logs that prove the service came up healthy must stay at Information, so a
        // deployment can be verified without turning on Debug. Per-station chatter may be demoted; this
        // may not. Pinned here because it is exactly the kind of line a log-noise cleanup quietly drops.
        var harness = new Harness(o => o.StartupVerificationStations = "02HA014");
        harness.Stations["CA"] = [new("02HA014", "ON", -5)];

        await RunHostedAsync(harness.Worker, TimeSpan.FromSeconds(2));

        IReadOnlyList<string> info = harness.Log.MessagesAt(LogLevel.Information);
        await Assert.That(info.Any(m => m.Contains("station 02HA014 processed successfully"))).IsTrue();
        await Assert.That(info.Any(m => m.Contains("Startup verification: SUCCESS"))).IsTrue();
    }

    [Test]
    public async Task StartupVerificationFailure_IsReportedAtError()
    {
        // The other half of the policy: a failed verification must be loud, not swallowed.
        var harness = new Harness(o => o.StartupVerificationStations = "02HA014");
        harness.Stations["CA"] = [new("02JE025", "QC", -5)]; // requested station is not in the list

        await RunHostedAsync(harness.Worker, TimeSpan.FromSeconds(2));

        await Assert.That(harness.Log.MessagesAt(LogLevel.Error).Any(m => m.Contains("FAILED"))).IsTrue();
    }

    [Test]
    public async Task RunOnStartup_AnnouncesItselfAtInformation()
    {
        var harness = new Harness(o => o.RunOnStartup = true);
        harness.Stations["CA"] = [new("02JE025", "QC", -5)];

        await RunHostedAsync(harness.Worker, TimeSpan.FromSeconds(2));

        await Assert.That(harness.Log.MessagesAt(LogLevel.Information).Any(m => m.Contains("RunOnStartup enabled")))
            .IsTrue();
    }

    [Test]
    public async Task StartupVerificationFailure_DoesNotPreventScheduling()
    {
        // A failed verification is reported, but the service must still come up and keep its schedule.
        var harness = new Harness(o => o.StartupVerificationStations = "02HA014");
        harness.StationLoadFailures["CA"] = new InvalidOperationException("db down");
        harness.StationLoadFailures["US"] = new InvalidOperationException("db down");

        Exception? escaped = await RunHostedAsync(harness.Worker, TimeSpan.FromSeconds(2));

        await Assert.That(escaped).IsNull();
        await Assert.That(harness.PostProcessing.CleanupRuns).IsEqualTo(1);
    }

    // --- helpers -------------------------------------------------------------------------------------

    /// <summary>Starts the worker as a hosted service, lets it settle, then stops it.</summary>
    private static async Task<Exception?> RunHostedAsync(StationWorker worker, TimeSpan settle)
    {
        try
        {
            await worker.StartAsync(CancellationToken.None);
            await Task.Delay(settle);
            await worker.StopAsync(CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<Exception> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("expected an exception, but none was thrown");
    }

    /// <summary>Wires a <see cref="StationWorker"/> over fakes and exposes what each one recorded.</summary>
    private sealed class Harness
    {
        public Harness(Action<WorkerOptions>? configure = null)
        {
            var options = new WorkerOptions
            {
                Cron = EveryHour,
                PauseBetweenStationsMs = 0, // no artificial delay unless a test asks for one
                Enabled = true,
            };
            configure?.Invoke(options);

            Repo = new FakeStationRepository(this);
            ProcessorCA = new FakeProcessorCA();
            ProcessorUS = new FakeProcessorUS();
            PostProcessing = new FakePostProcessing();
            BackoffRepo = new RecordingBackoffRepository(this);
            Metrics = new CountingMetrics();
            Log = new CapturingLogger();

            Worker = new StationWorker(
                Repo,
                ProcessorCA,
                ProcessorUS,
                PostProcessing,
                new StationHttp503BackoffService(BackoffRepo, NullLogger<StationHttp503BackoffService>.Instance),
                Metrics,
                Options.Create(options),
                Log);
        }

        public StationWorker Worker { get; }

        public FakeStationRepository Repo { get; }

        public FakeProcessorCA ProcessorCA { get; }

        public FakeProcessorUS ProcessorUS { get; }

        public FakePostProcessing PostProcessing { get; }

        public RecordingBackoffRepository BackoffRepo { get; }

        public CountingMetrics Metrics { get; }

        public CapturingLogger Log { get; }

        public Dictionary<string, List<StationRef>> Stations { get; } = new()
        {
            ["CA"] = [],
            ["US"] = [],
        };

        public Dictionary<string, Exception> StationLoadFailures { get; } = [];
    }

    private sealed class FakeStationRepository(Harness harness) : WaterStationRepository(null!)
    {
        public override Task<IReadOnlyList<StationRef>> FindSupportedAsync(string country, CancellationToken ct = default)
        {
            if (harness.StationLoadFailures.TryGetValue(country, out Exception? failure))
            {
                throw failure;
            }

            harness.BackoffRepo.Note("load-stations-after-refresh");
            return Task.FromResult<IReadOnlyList<StationRef>>(harness.Stations[country]);
        }
    }

    private sealed class FakeProcessorCA : StationProcessorCA
    {
        private readonly Recorder _recorder = new();

        public FakeProcessorCA() : base(null!, null!, null!, NullLogger<StationProcessorCA>.Instance) { }

        public Dictionary<string, ProcessingOutcome> Outcomes => _recorder.Outcomes;

        public IReadOnlyList<string> Seen => _recorder.Seen;

        public override Task<ProcessingOutcome> ProcessWithOutcomeAsync(
            string mli, string state, int tz, CancellationToken ct) =>
            Task.FromResult(_recorder.Record(mli));
    }

    private sealed class FakeProcessorUS : StationProcessorUS
    {
        private readonly Recorder _recorder = new();

        public FakeProcessorUS() : base(null!, null!, NullLogger<StationProcessorUS>.Instance) { }

        public Dictionary<string, ProcessingOutcome> Outcomes => _recorder.Outcomes;

        public IReadOnlyList<string> Seen => _recorder.Seen;

        public override Task<ProcessingOutcome> ProcessWithOutcomeAsync(
            string mli, string state, int tz, CancellationToken ct) =>
            Task.FromResult(_recorder.Record(mli));
    }

    /// <summary>Shared recording state — the two passes run in parallel, so this is lock-guarded.</summary>
    private sealed class Recorder
    {
        private readonly Lock _gate = new();
        private readonly List<string> _seen = [];

        public Dictionary<string, ProcessingOutcome> Outcomes { get; } = [];

        public IReadOnlyList<string> Seen
        {
            get { lock (_gate) { return _seen.ToArray(); } }
        }

        public ProcessingOutcome Record(string mli)
        {
            lock (_gate)
            {
                _seen.Add(mli);
            }

            return Outcomes.TryGetValue(mli, out ProcessingOutcome outcome) ? outcome : ProcessingOutcome.Processed;
        }
    }

    private sealed class FakePostProcessing()
        : StationPostProcessingService(null!, NullLogger<StationPostProcessingService>.Instance)
    {
        private int _speciesRuns;
        private int _cleanupRuns;

        public Exception? SpeciesFailure { get; set; }

        public Exception? CleanupFailure { get; set; }

        public int SpeciesRuns => Volatile.Read(ref _speciesRuns);

        public int CleanupRuns => Volatile.Read(ref _cleanupRuns);

        public override Task RunAfterStationProcessingAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _speciesRuns);
            return SpeciesFailure is null ? Task.CompletedTask : Task.FromException(SpeciesFailure);
        }

        public override Task CleanOldWaterDataAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _cleanupRuns);
            return CleanupFailure is null ? Task.CompletedTask : Task.FromException(CleanupFailure);
        }
    }

    private sealed class RecordingBackoffRepository(Harness harness) : IStationHttp503BackoffRepository
    {
        private readonly Lock _gate = new();
        private readonly List<string> _calls = [];
        private readonly List<string> _recorded = [];
        private readonly List<string> _reset = [];

        public IReadOnlyList<string> Calls
        {
            get { lock (_gate) { return _calls.ToArray(); } }
        }

        public IReadOnlyList<string> Recorded
        {
            get { lock (_gate) { return _recorded.ToArray(); } }
        }

        public IReadOnlyList<string> Reset
        {
            get { lock (_gate) { return _reset.ToArray(); } }
        }

        public void Note(string call)
        {
            lock (_gate)
            {
                _calls.Add(call);
            }
        }

        public Task RefreshDueAsync(DateOnly today, CancellationToken ct = default)
        {
            _ = harness;
            Note("refresh");
            return Task.CompletedTask;
        }

        public Task RecordHttp503Async(
            string provider, string country, string stationMli, string state, DateOnly today,
            CancellationToken ct = default)
        {
            lock (_gate)
            {
                _recorded.Add(stationMli);
            }

            return Task.CompletedTask;
        }

        public Task ResetAsync(string provider, string country, string stationMli, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _reset.Add(stationMli);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BackoffSummary>> SummaryByStateAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BackoffSummary>>(Array.Empty<BackoffSummary>());
    }

    /// <summary>Captures formatted log messages per level, so tests can assert on log LEVEL as policy.</summary>
    private sealed class CapturingLogger : ILogger<StationWorker>
    {
        private readonly Lock _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> MessagesAt(LogLevel level)
        {
            lock (_gate)
            {
                return _entries.Where(e => e.Level == level).Select(e => e.Message).ToArray();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    /// <summary>
    /// Counts stations per <c>(country, outcome-label)</c> pair — the same label the Prometheus counter
    /// would carry, so a test can assert that a skip is not filed under <c>failure</c>.
    /// </summary>
    private sealed class CountingMetrics : WaterMetrics
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<(string Country, string Outcome), int> _outcomes = [];
        private int _overruns;

        public int Overruns => Volatile.Read(ref _overruns);

        public int Successes(string country) => Counted(country, "success");

        public int Failures(string country) => Counted(country, "failure");

        public int Counted(string country, string outcomeLabel)
        {
            lock (_gate) { return _outcomes.GetValueOrDefault((country, outcomeLabel)); }
        }

        public override void StationProcessed(string country, ProcessingOutcome outcome)
        {
            var key = (country, WaterMetrics.OutcomeLabel(outcome));
            lock (_gate)
            {
                _outcomes[key] = _outcomes.GetValueOrDefault(key) + 1;
            }
        }

        public override void CycleOverrun() => Interlocked.Increment(ref _overruns);
    }
}
