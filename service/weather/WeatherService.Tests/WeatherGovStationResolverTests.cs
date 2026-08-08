using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using WeatherService.Data;
using WeatherService.Sources;
using static WeatherService.Tests.TestSupport;

namespace WeatherService.Tests;

/// <summary>
/// Covers the fix for the defect that made every US station skip: <c>WaterStation.MLI</c> is a water
/// gauge id, never an NWS call sign, so observations must be fetched for the station *nearest the
/// gauge's coordinate* — resolved once and cached, not re-asked every cycle.
/// </summary>
public class WeatherGovStationResolverTests
{
    private const string PointsResponse = """
        {"type":"FeatureCollection","features":[
          {"properties":{"stationIdentifier":"KPBF","name":"Pine Bluff"}},
          {"properties":{"stationIdentifier":"KLIT","name":"Little Rock"}}]}
        """;

    [Test]
    public async Task ParsesTheNearestStationIdentifier()
    {
        await Assert.That(WeatherGovFetcher.FirstStationIdentifier(PointsResponse)).IsEqualTo("KPBF");
    }

    [Test]
    public async Task EmptyFeatureCollectionMeansNoStation()
    {
        await Assert.That(WeatherGovFetcher.FirstStationIdentifier("""{"features":[]}""")).IsNull();
        await Assert.That(WeatherGovFetcher.FirstStationIdentifier("""{"other":1}""")).IsNull();
    }

    [Test]
    public async Task CacheMiss_CallsTheApiAndStoresTheAnswer()
    {
        var handler = new StubHandler(_ => Json(PointsResponse));
        var repository = new FakeRepository();
        WeatherGovStationResolver resolver = NewResolver(handler, repository);

        string? resolved = await resolver.ResolveAsync(Station("07263650", 34.1731, -91.9354, "AR"));

        await Assert.That(resolved).IsEqualTo("KPBF");
        await Assert.That(handler.RequestCount).IsEqualTo(1);
        await Assert.That(repository.Saved).IsEquivalentTo(new[] { ("07263650", "KPBF") });
        // The gauge's own coordinate is what gets asked about, rounded to the 4dp weather.gov accepts.
        await Assert.That(handler.LastRequest!.RequestUri!.AbsoluteUri)
            .Contains("/points/34.1731,-91.9354/stations");
    }

    [Test]
    public async Task CacheHit_DoesNotCallTheApi()
    {
        // The whole point of the cache: one resolution per gauge, not one per cycle.
        var handler = new StubHandler(_ => Json(PointsResponse));
        var repository = new FakeRepository { Cached = new WeatherGovStationRepository.CachedStation("KAUW") };
        WeatherGovStationResolver resolver = NewResolver(handler, repository);

        string? resolved = await resolver.ResolveAsync(Station("05398000", 44.8868, -89.6357, "WI"));

        await Assert.That(resolved).IsEqualTo("KAUW");
        await Assert.That(handler.RequestCount).IsZero();
        await Assert.That(repository.Saved).IsEmpty();
    }

    [Test]
    public async Task NegativeCacheHit_DoesNotCallTheApiEither()
    {
        // A stored row with no station means "asked, nothing nearby". Re-asking would burn a request
        // every cycle on a point that will never resolve.
        var handler = new StubHandler(_ => Json(PointsResponse));
        var repository = new FakeRepository { Cached = new WeatherGovStationRepository.CachedStation(null) };
        WeatherGovStationResolver resolver = NewResolver(handler, repository);

        string? resolved = await resolver.ResolveAsync(Station("99999999", 19.5, -155.5, "HI"));

        await Assert.That(resolved).IsNull();
        await Assert.That(handler.RequestCount).IsZero();
    }

    [Test]
    public async Task ApiReportsNoStation_IsCachedAsAMiss()
    {
        var handler = new StubHandler(_ => Json("""{"features":[]}"""));
        var repository = new FakeRepository();
        WeatherGovStationResolver resolver = NewResolver(handler, repository);

        string? resolved = await resolver.ResolveAsync(Station("99999999", 19.5, -155.5, "HI"));

        await Assert.That(resolved).IsNull();
        await Assert.That(repository.Saved).IsEquivalentTo(new[] { ("99999999", (string?)null) });
    }

    [Test]
    public async Task PointOutsideNwsCoverage_IsCachedAsAMissRatherThanFailing()
    {
        // /points 404s outside the US. That is a permanent answer, not an outage.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var repository = new FakeRepository();
        WeatherGovStationResolver resolver = NewResolver(handler, repository);

        string? resolved = await resolver.ResolveAsync(Station("CA-0001", 55.0, -105.0, "SK"));

        await Assert.That(resolved).IsNull();
        await Assert.That(repository.Saved).IsEquivalentTo(new[] { ("CA-0001", (string?)null) });
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static WeatherGovStationResolver NewResolver(HttpMessageHandler handler, FakeRepository repository)
    {
        WeatherGovFetcher fetcher = Fetcher(handler, (f, o, p, l) =>
            new WeatherGovFetcher(f, o, p, l, NullLogger<WeatherGovFetcher>.Instance));
        return new WeatherGovStationResolver(fetcher, repository, NullLogger<WeatherGovStationResolver>.Instance);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/geo+json"),
    };

    private sealed class FakeRepository() : WeatherGovStationRepository(null!, EmptyPipelines())
    {
        public WeatherGovStationRepository.CachedStation? Cached { get; init; }

        public List<(string Mli, string? StationId)> Saved { get; } = [];

        public override Task<CachedStation?> FindAsync(string mli, CancellationToken ct = default) =>
            Task.FromResult(Cached);

        public override Task SaveAsync(
            string mli, double latitude, double longitude, string? stationId, CancellationToken ct = default)
        {
            Saved.Add((mli, stationId));
            return Task.CompletedTask;
        }
    }
}
