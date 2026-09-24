using TUnit.Core;
using WaterApi.Domain;
using WaterApi.Services;

namespace WaterApi.Tests;

/// <summary>Covers <see cref="StationMapCache"/>'s load, single-flight and keep-last-good rules.</summary>
public class StationMapCacheTests
{
    [Test]
    public async Task Get_LoadsOnce_ThenServesFromMemory()
    {
        var repo = new FakeStationRepository();
        repo.Rows["US"] = [FakeStationRepository.Station(1, "US", "MN")];
        StationMapCache cache = TestSupport.NewCache(repo);

        StationMapSnapshot first = await cache.GetAsync("US", CancellationToken.None);
        StationMapSnapshot second = await cache.GetAsync("US", CancellationToken.None);

        await Assert.That(repo.Calls).IsEqualTo(1);
        await Assert.That(ReferenceEquals(second, first)).IsTrue();
        await Assert.That(first.Stations.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Get_ConcurrentColdCallers_ShareOneQuery()
    {
        var repo = new FakeStationRepository { Delay = TimeSpan.FromMilliseconds(200) };
        repo.Rows["CA"] = [FakeStationRepository.Station(7, "CA", "ON")];
        StationMapCache cache = TestSupport.NewCache(repo);

        StationMapSnapshot[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => cache.GetAsync("CA", CancellationToken.None)));

        await Assert.That(repo.Calls).IsEqualTo(1);
        await Assert.That(results.Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Refresh_Failure_KeepsPreviousSnapshot()
    {
        var repo = new FakeStationRepository();
        repo.Rows["US"] = [FakeStationRepository.Station(1, "US", "MN")];
        StationMapCache cache = TestSupport.NewCache(repo);
        StationMapSnapshot good = await cache.GetAsync("US", CancellationToken.None);

        repo.Failure = new InvalidOperationException("db down");
        bool available = await cache.RefreshAsync("US", CancellationToken.None);

        await Assert.That(available).IsTrue();
        await Assert.That(ReferenceEquals(await cache.GetAsync("US", CancellationToken.None), good)).IsTrue();
    }

    [Test]
    public async Task Get_NeverLoadedAndLoadFails_ThrowsUnavailable()
    {
        var repo = new FakeStationRepository { Failure = new InvalidOperationException("db down") };
        StationMapCache cache = TestSupport.NewCache(repo);

        await Assert.That(async () => await cache.GetAsync("US", CancellationToken.None))
            .Throws<StationCacheUnavailableException>();
    }

    [Test]
    public async Task Refresh_Success_ReplacesSnapshot()
    {
        var repo = new FakeStationRepository();
        repo.Rows["US"] = [FakeStationRepository.Station(1, "US", "MN")];
        StationMapCache cache = TestSupport.NewCache(repo);
        StationMapSnapshot before = await cache.GetAsync("US", CancellationToken.None);

        repo.Rows["US"] = [FakeStationRepository.Station(1, "US", "MN"), FakeStationRepository.Station(2, "US", "WI")];
        await cache.RefreshAsync("US", CancellationToken.None);
        StationMapSnapshot after = await cache.GetAsync("US", CancellationToken.None);

        await Assert.That(after.Stations.Count).IsEqualTo(2);
        await Assert.That(after.ETagFor(null)).IsNotEqualTo(before.ETagFor(null));
    }

    [Test]
    public async Task Countries_AreNormalizedAndDistinct_DefaultWhenEmpty()
    {
        var repo = new FakeStationRepository();

        StationMapCache configured = TestSupport.NewCache(repo,
            new Configuration.StationCacheOptions { Countries = ["ca", "US", " CA ", "us"] });
        StationMapCache unset = TestSupport.NewCache(repo, new Configuration.StationCacheOptions());

        await Assert.That(configured.Countries).IsEquivalentTo(new[] { "CA", "US" });
        await Assert.That(unset.Countries).IsEquivalentTo(new[] { "CA", "US" });
    }

    [Test]
    [Arguments(0UL, "0")]
    [Arguments(35UL, "Z")]
    [Arguments(36UL, "10")]
    [Arguments(263911UL, "5NMV")]
    public async Task ToBase36_MatchesLegacyViewMapEncoding(ulong value, string expected)
    {
        await Assert.That(MapStation.ToBase36(value)).IsEqualTo(expected);
    }
}
