using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Core;
using WeatherService.Configuration;
using WeatherService.Processing;

namespace WeatherService.Tests;

/// <summary>
/// Covers the ledger that stops a restart loop from re-spending a provider's paid daily quota.
/// Budget is charged per station, so an interrupted cycle costs only what it actually used.
/// </summary>
public class WeatherApiUsageTrackerTests
{
    private static readonly DateOnly Day = new(2026, 7, 15);

    [Test]
    public async Task ChargesOneStationAtATime()
    {
        WeatherApiUsageTracker tracker = Tracker(NewStateDir());

        await Assert.That(await tracker.TryConsumeAsync("weather-gov", Day, 3)).IsTrue();
        await Assert.That(await tracker.TryConsumeAsync("weather-gov", Day, 3)).IsTrue();

        WeatherApiUsageTracker.UsageSnapshot snapshot = await tracker.SnapshotAsync("weather-gov", Day, 3);
        await Assert.That(snapshot.UsedToday).IsEqualTo(2);
        await Assert.That(snapshot.Remaining).IsEqualTo(1);
    }

    [Test]
    public async Task StopsAtTheDailyLimit()
    {
        WeatherApiUsageTracker tracker = Tracker(NewStateDir());
        for (int i = 0; i < 3; i++)
        {
            await tracker.TryConsumeAsync("google-weather", Day, 3);
        }

        await Assert.That(await tracker.TryConsumeAsync("google-weather", Day, 3)).IsFalse();
        await Assert.That((await tracker.SnapshotAsync("google-weather", Day, 3)).UsedToday).IsEqualTo(3);
    }

    [Test]
    public async Task AnInterruptedCycleOnlyCostsWhatItUsed()
    {
        // The whole point of charging per station: the old up-front reservation booked the entire daily
        // limit at cycle start, so a restart 5 stations in forfeited the other 895.
        string stateDir = NewStateDir();
        WeatherApiUsageTracker before = Tracker(stateDir);
        for (int i = 0; i < 5; i++)
        {
            await before.TryConsumeAsync("weather-gov", Day, 900);
        }

        WeatherApiUsageTracker afterRestart = Tracker(stateDir);
        WeatherApiUsageTracker.UsageSnapshot snapshot = await afterRestart.SnapshotAsync("weather-gov", Day, 900);

        await Assert.That(snapshot.UsedToday).IsEqualTo(5);
        await Assert.That(snapshot.Remaining).IsEqualTo(895);
        await Assert.That(await afterRestart.TryConsumeAsync("weather-gov", Day, 900)).IsTrue();
    }

    [Test]
    public async Task KeepsProviderBudgetsSeparate()
    {
        WeatherApiUsageTracker tracker = Tracker(NewStateDir());
        await tracker.TryConsumeAsync("visual-crossing", Day, 1);

        await Assert.That(await tracker.TryConsumeAsync("visual-crossing", Day, 1)).IsFalse();
        await Assert.That(await tracker.TryConsumeAsync("weather-gov", Day, 1)).IsTrue();
    }

    [Test]
    public async Task YesterdaysUsageDoesNotCountAgainstToday()
    {
        string stateDir = NewStateDir();
        WeatherApiUsageTracker tracker = Tracker(stateDir);
        await tracker.TryConsumeAsync("open", Day.AddDays(-1), 1);

        await Assert.That((await tracker.SnapshotAsync("open", Day, 1)).UsedToday).IsZero();
        await Assert.That(await tracker.TryConsumeAsync("open", Day, 1)).IsTrue();
    }

    [Test]
    public async Task ZeroDailyLimitDisablesTheProvider()
    {
        await Assert.That(await Tracker(NewStateDir()).TryConsumeAsync("weather-canada", Day, 0)).IsFalse();
    }

    [Test]
    public async Task UnwritableStateDirSkipsTheProviderInsteadOfRunningUnmetered()
    {
        // Without a durable ledger there is no way to know what today already cost, so spend nothing.
        string parentIsAFile = Path.Combine(Path.GetTempPath(), "weather-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(parentIsAFile)!);
        await File.WriteAllTextAsync(parentIsAFile, "x");

        WeatherApiUsageTracker tracker = Tracker(Path.Combine(parentIsAFile, "state"));

        await Assert.That(await tracker.TryConsumeAsync("visual-crossing", Day, 1000)).IsFalse();
        await Assert.That((await tracker.SnapshotAsync("visual-crossing", Day, 1000)).Persisted).IsFalse();
    }

    private static WeatherApiUsageTracker Tracker(string stateDir) =>
        new(Options.Create(new LifecycleOptions { StateDir = stateDir }),
            NullLogger<WeatherApiUsageTracker>.Instance);

    private static string NewStateDir() =>
        Path.Combine(Path.GetTempPath(), "weather-tests", Guid.NewGuid().ToString("N"));
}
