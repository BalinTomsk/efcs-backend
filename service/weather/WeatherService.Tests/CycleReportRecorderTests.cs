using TUnit.Core;
using WeatherService.Reporting;

namespace WeatherService.Tests;

public class CycleReportRecorderTests
{
    [Test]
    public async Task StartsEmpty()
    {
        await Assert.That(new CycleReportRecorder().RecentEntries()).IsEmpty();
    }

    [Test]
    public async Task KeepsInsertionOrder()
    {
        var recorder = new CycleReportRecorder();
        recorder.Record(Entry(1));
        recorder.Record(Entry(2));

        await Assert.That(recorder.RecentEntries().Select(entry => entry.SuccessfulStations))
            .IsEquivalentTo(new[] { 1, 2 });
    }

    [Test]
    public async Task EvictsTheOldestBeyondTheCap()
    {
        // Bounded so a long-running process cannot grow this list without limit between weekly reports.
        var recorder = new CycleReportRecorder();
        for (int i = 1; i <= CycleReportRecorder.MaxEntries + 2; i++)
        {
            recorder.Record(Entry(i));
        }

        IReadOnlyList<CycleReportEntry> entries = recorder.RecentEntries();

        await Assert.That(entries).HasCount().EqualTo(CycleReportRecorder.MaxEntries);
        await Assert.That(entries[0].SuccessfulStations).IsEqualTo(3);
        await Assert.That(entries[^1].SuccessfulStations).IsEqualTo(CycleReportRecorder.MaxEntries + 2);
    }

    [Test]
    public async Task CapacityCoversAFullWeekForEveryProvider()
    {
        // MaxEntries is derived from the actual worker count so it always covers a full week no matter
        // how many providers exist -- the bug this replaced hardcoded the multiplier at 2 and silently
        // fell behind as providers were added (grew to 6 without the constant ever moving).
        await Assert.That(CycleReportRecorder.MaxEntries)
            .IsEqualTo(CycleReportRecorder.MaxEntriesPerWorker * WeatherService.Processing.StationWorker.WorkerCount);
    }

    private static CycleReportEntry Entry(int successfulStations) => new(
        DateOnly.FromDateTime(DateTime.Now), "Weather.gov", "US", successfulStations, 0,
        "MLI-" + successfulStations, null);
}
