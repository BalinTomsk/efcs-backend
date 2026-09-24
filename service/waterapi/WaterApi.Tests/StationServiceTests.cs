using TUnit.Core;
using WaterApi.Services;

namespace WaterApi.Tests;

/// <summary>Covers <see cref="StationService"/> input validation and filtering.</summary>
public class StationServiceTests
{
    private static (StationService Service, FakeStationRepository Repo) NewService()
    {
        var repo = new FakeStationRepository();
        repo.Rows["US"] =
        [
            FakeStationRepository.Station(1, "US", "MN"),
            FakeStationRepository.Station(2, "US", "WI"),
            FakeStationRepository.Station(3, "US", "MN"),
        ];
        repo.Rows["CA"] = [FakeStationRepository.Station(10, "CA", "ON")];
        return (new StationService(TestSupport.NewCache(repo)), repo);
    }

    [Test]
    public async Task GetMap_NormalizesCase_AndFiltersByState()
    {
        (StationService service, _) = NewService();

        StationMapView view = await service.GetMapAsync(" us ", "mn", CancellationToken.None);

        await Assert.That(view.Snapshot.Country).IsEqualTo("US");
        await Assert.That(view.State).IsEqualTo("MN");
        await Assert.That(view.Stations.Select(s => s.Sid)).IsEquivalentTo(new[] { 1, 3 });
    }

    [Test]
    public async Task GetMap_WithoutState_ReturnsWholeCountry()
    {
        (StationService service, _) = NewService();

        StationMapView view = await service.GetMapAsync("US", null, CancellationToken.None);

        await Assert.That(view.Stations.Count).IsEqualTo(3);
        await Assert.That(view.Stale).IsFalse();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("MX")]
    [Arguments("USA")]
    public async Task GetMap_BadCountry_IsInvalidRequest_AndNeverQueries(string? country)
    {
        (StationService service, FakeStationRepository repo) = NewService();

        await Assert.That(async () => await service.GetMapAsync(country, null, CancellationToken.None))
            .Throws<InvalidRequestException>();
        await Assert.That(repo.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("M")]
    [Arguments("MNN")]
    [Arguments("M1")]
    [Arguments("' OR 1=1 --")]
    public async Task GetMap_BadState_IsInvalidRequest(string state)
    {
        (StationService service, _) = NewService();

        await Assert.That(async () => await service.GetMapAsync("US", state, CancellationToken.None))
            .Throws<InvalidRequestException>();
    }

    [Test]
    public async Task GetStation_FindsAcrossCountries()
    {
        (StationService service, _) = NewService();

        await Assert.That((await service.GetStationAsync(10, CancellationToken.None)).Country).IsEqualTo("CA");
        await Assert.That((await service.GetStationAsync(2, CancellationToken.None)).State).IsEqualTo("WI");
    }

    [Test]
    public async Task GetStation_Unknown_IsNotFound()
    {
        (StationService service, _) = NewService();

        await Assert.That(async () => await service.GetStationAsync(999, CancellationToken.None))
            .Throws<StationNotFoundException>();
    }
}
