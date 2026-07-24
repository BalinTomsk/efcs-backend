using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using WaterService.Data;
using WaterService.Domain;
using WaterService.Processing;

namespace WaterService.Tests;

public class StationHttp503BackoffServiceTests
{
    private static readonly StationRef Station = new("02JE025", "QC", -5);

    [Test]
    public async Task RecordHttp503_ForwardsProviderCountryStationStateAndDate()
    {
        var repository = new FakeRepository();
        var service = new StationHttp503BackoffService(
            repository,
            NullLogger<StationHttp503BackoffService>.Instance);
        var today = new DateOnly(2026, 7, 21);

        await service.RecordHttp503Async("environment-canada", "CA", Station, today);

        await Assert.That(repository.RecordedProvider).IsEqualTo("environment-canada");
        await Assert.That(repository.RecordedCountry).IsEqualTo("CA");
        await Assert.That(repository.RecordedStationMli).IsEqualTo("02JE025");
        await Assert.That(repository.RecordedState).IsEqualTo("QC");
        await Assert.That(repository.RecordedToday).IsEqualTo(today);
    }

    [Test]
    public async Task RecordProcessed_ResetsStationBackoff()
    {
        var repository = new FakeRepository();
        var service = new StationHttp503BackoffService(
            repository,
            NullLogger<StationHttp503BackoffService>.Instance);

        await service.RecordProcessedAsync("environment-canada", "CA", Station);

        await Assert.That(repository.ResetProvider).IsEqualTo("environment-canada");
        await Assert.That(repository.ResetCountry).IsEqualTo("CA");
        await Assert.That(repository.ResetStationMli).IsEqualTo("02JE025");
    }

    [Test]
    public async Task RefreshDue_ForwardsDate()
    {
        var repository = new FakeRepository();
        var service = new StationHttp503BackoffService(
            repository,
            NullLogger<StationHttp503BackoffService>.Instance);
        var today = new DateOnly(2026, 7, 21);

        await service.RefreshDueAsync(today);

        await Assert.That(repository.RefreshedToday).IsEqualTo(today);
    }

    private sealed class FakeRepository : IStationHttp503BackoffRepository
    {
        public DateOnly? RefreshedToday { get; private set; }
        public string? RecordedProvider { get; private set; }
        public string? RecordedCountry { get; private set; }
        public string? RecordedStationMli { get; private set; }
        public string? RecordedState { get; private set; }
        public DateOnly? RecordedToday { get; private set; }
        public string? ResetProvider { get; private set; }
        public string? ResetCountry { get; private set; }
        public string? ResetStationMli { get; private set; }

        public Task RefreshDueAsync(DateOnly today, CancellationToken ct = default)
        {
            RefreshedToday = today;
            return Task.CompletedTask;
        }

        public Task RecordHttp503Async(
            string provider,
            string country,
            string stationMli,
            string state,
            DateOnly today,
            CancellationToken ct = default)
        {
            RecordedProvider = provider;
            RecordedCountry = country;
            RecordedStationMli = stationMli;
            RecordedState = state;
            RecordedToday = today;
            return Task.CompletedTask;
        }

        public Task ResetAsync(string provider, string country, string stationMli, CancellationToken ct = default)
        {
            ResetProvider = provider;
            ResetCountry = country;
            ResetStationMli = stationMli;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BackoffSummary>> SummaryByStateAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BackoffSummary>>(Array.Empty<BackoffSummary>());
    }
}
