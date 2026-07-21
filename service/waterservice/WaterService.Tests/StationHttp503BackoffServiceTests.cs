using Microsoft.Extensions.Logging.Abstractions;
using WaterService.Data;
using WaterService.Domain;
using WaterService.Processing;
using Xunit;

namespace WaterService.Tests;

public class StationHttp503BackoffServiceTests
{
    private static readonly StationRef Station = new("02JE025", "QC", -5);

    [Fact]
    public async Task RecordHttp503_ForwardsProviderCountryStationStateAndDate()
    {
        var repository = new FakeRepository();
        var service = new StationHttp503BackoffService(
            repository,
            NullLogger<StationHttp503BackoffService>.Instance);
        var today = new DateOnly(2026, 7, 21);

        await service.RecordHttp503Async("environment-canada", "CA", Station, today);

        Assert.Equal("environment-canada", repository.RecordedProvider);
        Assert.Equal("CA", repository.RecordedCountry);
        Assert.Equal("02JE025", repository.RecordedStationMli);
        Assert.Equal("QC", repository.RecordedState);
        Assert.Equal(today, repository.RecordedToday);
    }

    [Fact]
    public async Task RecordProcessed_ResetsStationBackoff()
    {
        var repository = new FakeRepository();
        var service = new StationHttp503BackoffService(
            repository,
            NullLogger<StationHttp503BackoffService>.Instance);

        await service.RecordProcessedAsync("environment-canada", "CA", Station);

        Assert.Equal("environment-canada", repository.ResetProvider);
        Assert.Equal("CA", repository.ResetCountry);
        Assert.Equal("02JE025", repository.ResetStationMli);
    }

    [Fact]
    public async Task RefreshDue_ForwardsDate()
    {
        var repository = new FakeRepository();
        var service = new StationHttp503BackoffService(
            repository,
            NullLogger<StationHttp503BackoffService>.Instance);
        var today = new DateOnly(2026, 7, 21);

        await service.RefreshDueAsync(today);

        Assert.Equal(today, repository.RefreshedToday);
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
