using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Core;
using WeatherService.Configuration;
using WeatherService.Reporting;

namespace WeatherService.Tests;

/// <summary>
/// Covers crash detection: a run that ended without its shutdown hook is reported as an incident on the
/// next startup, and a graceful stop is not.
/// </summary>
public class ServiceLifecycleTrackerTests
{
    [Test]
    public async Task FirstEverStartup_RecordsNoIncident()
    {
        string stateDir = NewDir();
        ServiceLifecycleTracker tracker = Tracker(stateDir, Path.Combine(NewDir(), "missing.log"));

        tracker.Init();

        await Assert.That(tracker.RecentIncidents()).IsEmpty();
    }

    [Test]
    public async Task CleanShutdownThenRestart_RecordsNoIncident()
    {
        string stateDir = NewDir();
        string logFile = await WriteLogAsync(string.Empty);

        ServiceLifecycleTracker first = Tracker(stateDir, logFile);
        first.Init();
        first.OnShutdown(); // graceful stop -- writes CLEAN

        ServiceLifecycleTracker second = Tracker(stateDir, logFile);
        second.Init();

        await Assert.That(second.RecentIncidents()).IsEmpty();
    }

    [Test]
    public async Task UncleanShutdown_RecordsAnIncidentDescribedByTheLastErrorLogged()
    {
        string stateDir = NewDir();
        string logFile = await WriteLogAsync(string.Join('\n',
            """{"@t":"2026-07-07T23:00:00.0000000Z","@m":"Processed station."}""",
            """{"@t":"2026-07-07T23:45:00.0000000Z","@l":"Error","@m":"Weather worker loop failed."}""",
            string.Empty));

        ServiceLifecycleTracker crashed = Tracker(stateDir, logFile);
        crashed.Init(); // no OnShutdown() -- simulates a crash: the marker stays RUNNING

        ServiceLifecycleTracker restarted = Tracker(stateDir, logFile);
        restarted.Init();

        IReadOnlyList<IncidentEntry> incidents = restarted.RecentIncidents();
        await Assert.That(incidents).HasCount().EqualTo(1);
        await Assert.That(incidents[0].Description).Contains("ERROR");
        await Assert.That(incidents[0].Description).Contains("Weather worker loop failed");
        await Assert.That(incidents[0].DowntimeStart)
            .IsEqualTo(new DateTime(2026, 7, 7, 23, 45, 0, DateTimeKind.Utc).ToLocalTime());
    }

    [Test]
    public async Task CrashWithNoLogData_StillRecordsAnIncident()
    {
        // The crash matters even when nothing explains it — an unreported crash is the worst outcome.
        string stateDir = NewDir();
        string missingLog = Path.Combine(NewDir(), "does-not-exist.log");

        Tracker(stateDir, missingLog).Init();
        Tracker(stateDir, missingLog).Init();

        IReadOnlyList<IncidentEntry> incidents = Tracker(stateDir, missingLog).RecentIncidents();

        await Assert.That(incidents).HasCount().EqualTo(1);
        await Assert.That(incidents[0].Description).IsEqualTo("no log data available");
    }

    [Test]
    public async Task IncidentsOlderThanTheRetentionWindow_AreDropped()
    {
        string stateDir = NewDir();
        Directory.CreateDirectory(stateDir);
        await File.WriteAllTextAsync(
            Path.Combine(stateDir, "incidents.log"),
            IncidentLine(DateTime.Now.AddDays(-10), "stale incident")
            + IncidentLine(DateTime.Now.AddDays(-1), "recent incident"),
            Encoding.UTF8);

        IReadOnlyList<IncidentEntry> incidents =
            Tracker(stateDir, Path.Combine(NewDir(), "missing.log")).RecentIncidents();

        await Assert.That(incidents).HasCount().EqualTo(1);
        await Assert.That(incidents[0].Description).IsEqualTo("recent incident");
    }

    [Test]
    public async Task UnwritableStateDir_DisablesTrackingWithoutFailingStartup()
    {
        // Crash reporting is a nice-to-have; it must never be the reason the service will not boot.
        string parentIsAFile = Path.Combine(NewDir(), "not-a-directory");
        Directory.CreateDirectory(Path.GetDirectoryName(parentIsAFile)!);
        await File.WriteAllTextAsync(parentIsAFile, "x");

        ServiceLifecycleTracker tracker =
            Tracker(Path.Combine(parentIsAFile, "state"), Path.Combine(NewDir(), "missing.log"));

        tracker.Init(); // must not throw

        await Assert.That(tracker.RecentIncidents()).IsEmpty();
    }

    private static ServiceLifecycleTracker Tracker(string stateDir, string logFile) =>
        new(Options.Create(new LifecycleOptions { StateDir = stateDir, LogFile = logFile }),
            NullLogger<ServiceLifecycleTracker>.Instance);

    private static string NewDir() =>
        Path.Combine(Path.GetTempPath(), "weather-tests", Guid.NewGuid().ToString("N"));

    private static async Task<string> WriteLogAsync(string content)
    {
        string dir = NewDir();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "weather.log");
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
        return path;
    }

    private static string IncidentLine(DateTime at, string description)
    {
        string stamp = at.ToString("O", CultureInfo.InvariantCulture);
        return string.Join('|', stamp, stamp, stamp, description) + Environment.NewLine;
    }
}
