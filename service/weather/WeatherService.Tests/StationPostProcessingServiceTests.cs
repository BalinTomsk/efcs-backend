using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using WeatherService.Data;
using WeatherService.Processing;
using static WeatherService.Tests.TestSupport;

namespace WeatherService.Tests;

public class StationPostProcessingServiceTests
{
    [Test]
    public async Task RecordsTheCycleThenCleans_AndNeverTouchesTheFishModel()
    {
        // Since 11.0.0 the fish hand-over (species push, probability update) is env_service's job: it reads job_run.
        var repository = new RecordingRepository();
        var service = new StationPostProcessingService(repository, NullLogger<StationPostProcessingService>.Instance);

        await service.RunAfterStationProcessingAsync("Open", "CA", DateTime.UtcNow, succeeded: 12, failed: 1);

        await Assert.That(repository.Calls).IsEquivalentTo(new[]
        {
            "sp_job_run_record open-ca partial 12",
            "sp_clean_old_weather_data",
        });
    }

    [Test]
    public async Task ACycleThatSavedNothing_IsNotRecorded_ButStillCleans()
    {
        var repository = new RecordingRepository();
        var service = new StationPostProcessingService(repository, NullLogger<StationPostProcessingService>.Instance);

        await service.RunAfterStationProcessingAsync("weather-gov", "US", DateTime.UtcNow, succeeded: 0, failed: 0);

        await Assert.That(repository.Calls).IsEquivalentTo(new[] { "sp_clean_old_weather_data" });
    }

    [Test]
    public async Task AFailingStep_StopsTheRunAndSurfaces()
    {
        // The caller decides what a post-processing failure means; swallowing it here would hide it.
        var repository = new RecordingRepository { FailOn = "sp_job_run_record" };
        var service = new StationPostProcessingService(repository, NullLogger<StationPostProcessingService>.Instance);

        await Assert.That(() => service.RunAfterStationProcessingAsync("Open", "CA", DateTime.UtcNow, 3, 0))
            .Throws<InvalidOperationException>();
        await Assert.That(repository.Calls).HasCount().EqualTo(1);
    }

    [Test]
    public async Task JobName_IsLowerCaseAndFitsTheColumn()
    {
        await Assert.That(StationPostProcessingService.JobName("Open", "CA")).IsEqualTo("open-ca");
        await Assert.That(StationPostProcessingService.JobName(new string('x', 40), "US").Length).IsEqualTo(32);
    }

    private sealed class RecordingRepository()
        : WeatherDataRepository(null!, EmptyPipelines(), NullLogger<WeatherDataRepository>.Instance)
    {
        public List<string> Calls { get; } = [];

        public string? FailOn { get; init; }

        public override Task RecordCycleAsync(
            string job, DateTime startedUtc, DateTime finishedUtc, string status, int items, string? note,
            CancellationToken ct = default) =>
            Record("sp_job_run_record", $" {job} {status} {items}");

        public override Task CleanOldWeatherDataAsync(CancellationToken ct = default) =>
            Record("sp_clean_old_weather_data", string.Empty);

        private Task Record(string procedure, string detail)
        {
            Calls.Add(procedure + detail);
            return procedure == FailOn
                ? Task.FromException(new InvalidOperationException(procedure + " failed"))
                : Task.CompletedTask;
        }
    }
}
