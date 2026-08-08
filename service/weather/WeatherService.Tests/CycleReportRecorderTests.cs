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

    private static CycleReportEntry Entry(int successfulStations) => new(
        DateOnly.FromDateTime(DateTime.Now), "Weather.gov", "US", successfulStations, 0,
        "MLI-" + successfulStations, null);
}
