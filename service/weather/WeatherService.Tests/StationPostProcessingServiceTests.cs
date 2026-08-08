using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using WeatherService.Data;
using WeatherService.Processing;
using static WeatherService.Tests.TestSupport;

namespace WeatherService.Tests;

public class StationPostProcessingServiceTests
{
    [Test]
    public async Task RunsTheThreeProceduresInOrder()
    {
        // Order is not cosmetic: probabilities are recomputed from the species pushed by the first
        // procedure, and the cleanup must not delete rows the second one still needs.
        var repository = new RecordingRepository();
        var service = new StationPostProcessingService(repository, NullLogger<StationPostProcessingService>.Instance);

        await service.RunAfterStationProcessingAsync();

        await Assert.That(repository.Calls).IsEquivalentTo(new[]
        {
            "spPushSpeciesFromLakeToStation",
            "spTotalUpdateProbability",
            "sp_clean_old_weather_data",
        });
    }

    [Test]
    public async Task AFailingProcedure_StopsTheRunAndSurfaces()
    {
        // The caller decides what a post-processing failure means; swallowing it here would hide it.
        var repository = new RecordingRepository { FailOn = "spTotalUpdateProbability" };
        var service = new StationPostProcessingService(repository, NullLogger<StationPostProcessingService>.Instance);

        await Assert.That(() => service.RunAfterStationProcessingAsync()).Throws<InvalidOperationException>();
        await Assert.That(repository.Calls).HasCount().EqualTo(2);
    }

    private sealed class RecordingRepository()
        : WeatherDataRepository(null!, EmptyPipelines(), NullLogger<WeatherDataRepository>.Instance)
    {
        public List<string> Calls { get; } = [];

        public string? FailOn { get; init; }

        public override Task PushSpeciesFromLakeToStationAsync(CancellationToken ct = default) =>
            Record("spPushSpeciesFromLakeToStation");

        public override Task TotalUpdateProbabilityAsync(CancellationToken ct = default) =>
            Record("spTotalUpdateProbability");

        public override Task CleanOldWeatherDataAsync(CancellationToken ct = default) =>
            Record("sp_clean_old_weather_data");

        private Task Record(string procedure)
        {
            Calls.Add(procedure);
            return procedure == FailOn
                ? Task.FromException(new InvalidOperationException(procedure + " failed"))
                : Task.CompletedTask;
        }
    }
}
