using System.Runtime.CompilerServices;
using EnvService.Configuration;
using EnvService.Data;
using EnvService.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;

namespace EnvService.Tests;

internal sealed class FakeEnv : IEnvStore
{
    public List<PendingJob> Pending { get; } = [];
    public List<StationStateRow> States { get; } = [];
    public List<StationDayRow> Days { get; } = [];
    public List<long> Marked { get; } = [];
    public List<int> Mirrored { get; } = [];
    public int FlowCalls;
    public int PressureCalls;

    public Task<int> MirrorStationsAsync(IReadOnlyList<StationMaster> stations, CancellationToken ct)
    {
        Mirrored.Add(stations.Count);
        return Task.FromResult(stations.Count);
    }

    public Task<FillResult> FillFlowAsync(CancellationToken ct)
    {
        FlowCalls++;
        return Task.FromResult(new FillResult(7));
    }

    public Task<FillResult> FillPressureAsync(CancellationToken ct)
    {
        PressureCalls++;
        return Task.FromResult(new FillResult(0));
    }

    public Task<IReadOnlyList<PendingJob>> PendingJobsAsync(CancellationToken ct) =>
        Task.FromResult((IReadOnlyList<PendingJob>)Pending.ToList());

    public Task<int> MarkHandedOverAsync(long upToJobRunId, CancellationToken ct)
    {
        Marked.Add(upToJobRunId);
        int n = Pending.RemoveAll(p => p.JobRunId <= upToJobRunId);
        return Task.FromResult(n);
    }

    public Task<IReadOnlyList<StationStateRow>> StationStatesAsync(CancellationToken ct) =>
        Task.FromResult((IReadOnlyList<StationStateRow>)States.ToList());

    public async IAsyncEnumerable<StationDayRow> StationDaysAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (StationDayRow d in Days)
        {
            await Task.Yield();
            yield return d;
        }
    }
}

internal sealed class FakeFish : IFishStore
{
    public List<int> StateBatches { get; } = [];
    public List<int> DayBatches { get; } = [];
    public List<DateOnly> Trims { get; } = [];
    public List<StationMaster> Stations { get; } = [];
    public Exception? FailDays { get; set; }

    public Task<IReadOnlyList<StationMaster>> StationListAsync(CancellationToken ct) =>
        Task.FromResult((IReadOnlyList<StationMaster>)Stations.ToList());

    public Task<int> PushStationStatesAsync(IReadOnlyList<StationStateRow> rows, CancellationToken ct)
    {
        StateBatches.Add(rows.Count);
        return Task.FromResult(rows.Count);
    }

    public Task<int> PushStationDaysAsync(IReadOnlyList<StationDayRow> rows, CancellationToken ct)
    {
        if (FailDays is not null)
        {
            return Task.FromException<int>(FailDays);
        }
        DayBatches.Add(rows.Count);
        return Task.FromResult(rows.Count);
    }

    public Task<int> TrimStationDaysAsync(DateOnly from, CancellationToken ct)
    {
        Trims.Add(from);
        return Task.FromResult(3);
    }
}

public class HandOverJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static (HandOverJob Job, FakeEnv Env, FakeFish Fish, JobStatus Status, FakeTimeProvider Clock) Make(int batch = 2)
    {
        var env = new FakeEnv();
        var fish = new FakeFish();
        var status = new JobStatus();
        var clock = new FakeTimeProvider(Now);
        var job = new HandOverJob(env, fish, status,
            Options.Create(new EnvOptions { PushBatchSize = batch, HandOverMaxAgeMinutes = 60 }), clock,
            NullLogger<HandOverJob>.Instance);
        return (job, env, fish, status, clock);
    }

    private static StationStateRow State(int sid) =>
        new(sid, "M" + sid, Now.UtcDateTime, 1, null, 8, null, 1, "STABLE", 1, 1, 1, 1, null, Now.UtcDateTime);

    private static StationDayRow Day(int sid, int d) =>
        new(sid, DateOnly.FromDateTime(Now.UtcDateTime).AddDays(d), 5, 7, 2, 10, 1, 755.3m, "STABLE", null, null, 1, 1, 1);

    [Test]
    public async Task PendingCycles_ArePushedInBatches_TrimmedAndMarkedUpToTheNewest()
    {
        var (job, env, fish, status, _) = Make(batch: 2);
        env.Pending.AddRange([new(10, "waterservice", "water-ca", Now.UtcDateTime), new(12, "weather", "open-ca", Now.UtcDateTime)]);
        env.States.AddRange([State(1), State(2), State(3)]);
        env.Days.AddRange([Day(1, 0), Day(1, 1), Day(2, 0), Day(2, 1), Day(3, 0)]);

        await job.RunOnceAsync(force: false, CancellationToken.None);

        await Assert.That(fish.StateBatches).IsEquivalentTo(new[] { 2, 1 });
        await Assert.That(fish.DayBatches).IsEquivalentTo(new[] { 2, 2, 1 });
        await Assert.That(fish.Trims).IsEquivalentTo(new[] { new DateOnly(2026, 11, 22) });
        await Assert.That(env.Marked).IsEquivalentTo(new[] { 12L });
        await Assert.That(env.FlowCalls).IsEqualTo(1);
        await Assert.That(env.PressureCalls).IsEqualTo(1);
        await Assert.That(status.Get(HandOverJob.Name)!.LastOk).IsEqualTo(true);
        await Assert.That(status.Get(HandOverJob.Name)!.LastResult!).Contains("cycles=2 flow=7 pressure=0-or-fresh states=3 days=5");
    }

    [Test]
    public async Task NothingPendingAndRecent_DoesNothing()
    {
        var (job, env, fish, _, clock) = Make();
        await job.RunOnceAsync(force: false, CancellationToken.None);     // first run: never handed over -> runs
        fish.StateBatches.Clear();
        env.FlowCalls = 0;
        clock.Advance(TimeSpan.FromMinutes(10));

        await job.RunOnceAsync(force: false, CancellationToken.None);

        await Assert.That(env.FlowCalls).IsEqualTo(0);
        await Assert.That(fish.Trims).HasCount().EqualTo(1);
    }

    [Test]
    public async Task NothingPendingButOlderThanMaxAge_RunsAnyway()
    {
        // The forecast window moves at midnight even when no collector finished a cycle.
        var (job, env, _, _, clock) = Make();
        await job.RunOnceAsync(force: false, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(61));

        await job.RunOnceAsync(force: false, CancellationToken.None);

        await Assert.That(env.FlowCalls).IsEqualTo(2);
    }

    [Test]
    public async Task Force_RunsWithNothingPending_AndMarksNothing()
    {
        var (job, env, _, _, _) = Make();
        await job.RunOnceAsync(force: false, CancellationToken.None);
        await job.RunOnceAsync(force: true, CancellationToken.None);

        await Assert.That(env.FlowCalls).IsEqualTo(2);
        await Assert.That(env.Marked).HasCount().EqualTo(0);
    }

    [Test]
    public async Task AFailedPush_LeavesTheCyclesPending_AndIsRecorded()
    {
        var (job, env, fish, status, _) = Make();
        env.Pending.Add(new(5, "weather", "open-ca", Now.UtcDateTime));
        env.Days.Add(Day(1, 0));
        fish.FailDays = new TimeoutException("SQL Server timeout");

        JobRun? run = await job.RunOnceAsync(force: false, CancellationToken.None);

        await Assert.That(env.Marked).HasCount().EqualTo(0);
        await Assert.That(env.Pending).HasCount().EqualTo(1);
        await Assert.That(run!.LastOk).IsEqualTo(false);
        await Assert.That(run.Failures).IsEqualTo(1L);
        await Assert.That(run.LastResult!).Contains("SQL Server timeout");
    }

    [Test]
    public async Task Describe_NamesSkippedAndLockedFills()
    {
        // 0 = skipped (younger than 6 h) OR ran with no station qualifying: the procedure returns 0 for both.
        await Assert.That(HandOverJob.Describe(new FillResult(0))).IsEqualTo("0-or-fresh");
        await Assert.That(HandOverJob.Describe(new FillResult(-1))).IsEqualTo("locked");
        await Assert.That(HandOverJob.Describe(new FillResult(42))).IsEqualTo("42");
    }
}

public class StationMirrorJobTests
{
    [Test]
    public async Task Mirror_SendsTheWholeMasterList_AndRecordsIt()
    {
        var env = new FakeEnv();
        var fish = new FakeFish();
        fish.Stations.Add(new StationMaster(1, "A", Guid.NewGuid(), "CA", "ON", 45, -79, -5, 2, "x", null, "", null, null,
            true, true, DateTime.UtcNow));
        fish.Stations.Add(fish.Stations[0] with { Sid = 2, Mli = "B" });
        var status = new JobStatus();
        var job = new StationMirrorJob(fish, env, status, Options.Create(new EnvOptions()),
            NullLogger<StationMirrorJob>.Instance);

        JobRun? run = await job.RunOnceAsync(CancellationToken.None);

        await Assert.That(env.Mirrored).IsEquivalentTo(new[] { 2 });
        await Assert.That(run!.LastOk).IsEqualTo(true);
        await Assert.That(run.LastResult!).StartsWith("stations=2");
    }
}
