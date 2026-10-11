using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.Registry;
using TUnit.Core;
using WaterService.Configuration;
using WaterService.Domain;
using WaterService.Sources;

namespace WaterService.Tests;

/// <summary>
/// Covers how <see cref="UsgsWaterDataClient"/> pages the USGS snapshot, authenticates, and classifies
/// transport failures.
/// </summary>
public class UsgsWaterDataClientTests
{
    private const string Base = "https://usgs.test/ogcapi/v1/collections";

    [Test]
    public async Task FetchLatest_FollowsNextLinks_AndKeysByBareSiteNumber()
    {
        var requests = new List<HttpRequestMessage>();
        var client = NewClient(new StubHandler(request =>
        {
            requests.Add(request);
            return requests.Count == 1
                ? Json(PageWithNext([Feature("USGS-08313000", "00060", "12.5")], Base + "/latest-continuous/items?cursor=2"))
                : Json(Page(Feature("USGS-08313000", "00065", "3.3"), Feature("USGS-05112000", "00060", "40")));
        }), apiKey: "k");

        IReadOnlyDictionary<string, List<UsgsObservation>> snapshot = await client.FetchLatestAsync(null);

        await Assert.That(requests).HasCount().EqualTo(2);
        await Assert.That(requests[1].RequestUri!.Query).Contains("cursor=2");
        await Assert.That(requests.All(r => r.Headers.GetValues("X-Api-Key").Single() == "k")).IsTrue();
        await Assert.That(snapshot["08313000"]).HasCount().EqualTo(2);
        await Assert.That(snapshot["05112000"].Single().Value).IsEqualTo("40");
    }

    [Test]
    public async Task FetchLatest_SingleStation_FiltersBySite()
    {
        HttpRequestMessage? seen = null;
        var client = NewClient(new StubHandler(request =>
        {
            seen = request;
            return Json(Page());
        }));

        await client.FetchLatestAsync("08313000");

        await Assert.That(seen!.RequestUri!.Query).Contains("monitoring_location_id=USGS-08313000");
        await Assert.That(seen.Headers.Contains("X-Api-Key")).IsFalse();
    }

    [Test]
    public async Task FetchLatest_RateLimited_FailsNamingTheKey()
    {
        var client = NewClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));

        HttpRequestException thrown = await CaptureAsync(() => client.FetchLatestAsync(null));

        await Assert.That(thrown.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(thrown.Message).Contains("USGS_API_KEY");
    }

    [Test]
    public async Task FetchLatest_BodyCutOff_IsRetryableHttpFailure()
    {
        var client = NewClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ThrowingContent(),
        }));

        await Assert.That(() => client.FetchLatestAsync(null)).Throws<HttpRequestException>();
    }

    [Test]
    public async Task EnsureTimeZones_LooksUpEachSiteOnce()
    {
        int calls = 0;
        var client = NewClient(new StubHandler(_ =>
        {
            calls++;
            return Json("""
                {"type":"FeatureCollection","features":[
                  {"type":"Feature","id":"USGS-09380000",
                   "properties":{"time_zone_abbreviation":"MST","uses_daylight_savings":"N"}}]}
                """);
        }));

        await client.EnsureTimeZonesAsync(["09380000", "00000000"]);
        await client.EnsureTimeZonesAsync(["09380000", "00000000"]);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(client.TimeZoneFor("09380000").BaseUtcOffset).IsEqualTo(TimeSpan.FromHours(-7));
        await Assert.That(client.TimeZoneFor("00000000")).IsEqualTo(TimeZoneInfo.Utc);
    }

    // --- helpers -----------------------------------------------------------------------------------

    /// <summary>Builds a client over a stub transport and an EMPTY pipeline, so no real retry delays.</summary>
    private static UsgsWaterDataClient NewClient(HttpMessageHandler handler, string? apiKey = null)
    {
        var registry = new ResiliencePipelineRegistry<string>();
        registry.TryAddBuilder(ResiliencePipelines.UsFeed, (_, _) => { });
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["USGS_API_KEY"] = apiKey })
            .Build();

        return new UsgsWaterDataClient(
            new StubHttpClientFactory(handler),
            registry,
            Options.Create(new WorkerOptions { UsgsBaseUrl = Base }),
            configuration,
            NullLogger<UsgsWaterDataClient>.Instance);
    }

    private static string Feature(string site, string code, string value) => $$$"""
        {"type":"Feature","properties":{"monitoring_location_id":"{{{site}}}","parameter_code":"{{{code}}}",
         "statistic_id":"00011","time":"2026-10-11T04:00:00+00:00","value":"{{{value}}}"}}
        """;

    private static string Page(params string[] features) => PageWithNext(features, null);

    private static string PageWithNext(string[] features, string? next)
    {
        string links = next is null ? "" : "{\"rel\":\"next\",\"href\":\"" + next + "\"}";
        return "{\"type\":\"FeatureCollection\",\"features\":[" + string.Join(",", features)
            + "],\"links\":[" + links + "]}";
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static async Task<HttpRequestException> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (HttpRequestException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("expected an HttpRequestException, but none was thrown");
    }

    /// <summary>Response content whose body read fails, like a connection dropped mid-payload.</summary>
    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException("The response ended prematurely.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
