using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using NewsSearch.Configuration;
using NewsSearch.Sources;
using TUnit.Core;

namespace NewsSearch.Tests;

public class NewsSearchTests
{
    private const string NewsIdxAnswer = """
        {"data":{"query":"walleye","country":null,"count":2,"items":[
          {"newsId":"a1","title":"Walleye limits","source":"CTV","country":"CA","date":"2026-09-01","score":0.81,"snippet":"…","lakeId":null},
          {"newsId":"b2","title":"Bass derby","source":null,"country":"US","date":"2026-08-02","score":0.62,"snippet":null,"lakeId":"l1"}]},
         "error":null,"meta":{}}
        """;

    [Test]
    public async Task Parse_MapsNewsIdxItems()
    {
        IReadOnlyList<SearchHit> hits = VectorSource.Parse(JsonDocument.Parse(NewsIdxAnswer).RootElement);

        await Assert.That(hits.Count).IsEqualTo(2);
        await Assert.That(hits[0].NewsId).IsEqualTo("a1");
        await Assert.That(hits[0].Score).IsEqualTo(0.81);
        await Assert.That(hits[1].LakeId).IsEqualTo("l1");
    }

    [Test]
    public async Task Parse_UnexpectedShape_IsEmpty()
    {
        await Assert.That(VectorSource.Parse(JsonDocument.Parse("""{"data":null}""").RootElement)).IsEmpty();
    }

    [Test]
    [Arguments(null, 10, null)]
    [Arguments("  ", 10, null)]
    [Arguments("ok", 0, null)]
    [Arguments("ok", 51, null)]
    [Arguments("ok", 10, "CAN")]
    public async Task Validate_BadInput_IsInvalidRequest(string? q, int limit, string? country)
    {
        var service = new SearchService([], Options.Create(new NewsSearchOptions()));
        await Assert.That(() => service.Validate(q, limit, country)).Throws<InvalidRequestException>();
    }

    [Test]
    public async Task Validate_Normalizes()
    {
        var service = new SearchService([], Options.Create(new NewsSearchOptions()));
        SearchRequest r = service.Validate("  pike  ", null, "ca");
        await Assert.That(r).IsEqualTo(new SearchRequest("pike", 10, "CA"));
    }

    [Test]
    public async Task Http_VectorSource_ReturnsHits_AndForwardsTheQuery()
    {
        var upstream = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(NewsIdxAnswer, System.Text.Encoding.UTF8, "application/json"),
        });
        await using WebApplicationFactory<Program> factory = Factory(upstream);
        HttpClient client = factory.CreateClient();

        HttpResponseMessage ok = await client.GetAsync("/api/v1/newssearch/vector?q=walleye%20limits&limit=2&country=ca");
        JsonElement body = await ok.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(ok.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("data").GetProperty("source").GetString()).IsEqualTo("vector");
        await Assert.That(body.GetProperty("data").GetProperty("count").GetInt32()).IsEqualTo(2);
        await Assert.That(upstream.LastUri!.PathAndQuery).IsEqualTo("/api/v1/newsidx/search?q=walleye%20limits&limit=2&country=CA");
    }

    [Test]
    public async Task Http_Bm25Source_CallsTheBm25Route()
    {
        var upstream = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(NewsIdxAnswer, System.Text.Encoding.UTF8, "application/json"),
        });
        await using WebApplicationFactory<Program> factory = Factory(upstream);

        HttpResponseMessage ok = await factory.CreateClient().GetAsync("/api/v1/newssearch/bm25?q=keta&limit=3");
        JsonElement body = await ok.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(ok.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("data").GetProperty("source").GetString()).IsEqualTo("bm25");
        await Assert.That(upstream.LastUri!.PathAndQuery).IsEqualTo("/api/v1/newsidx/bm25?q=keta&limit=3");
    }

    [Test]
    public async Task Http_SourcesList_UnknownSource_BadQuery()
    {
        await using WebApplicationFactory<Program> factory = Factory(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        HttpClient client = factory.CreateClient();

        JsonElement sources = await client.GetFromJsonAsync<JsonElement>("/api/v1/newssearch/sources");
        HttpResponseMessage unknown = await client.GetAsync("/api/v1/newssearch/image?q=x");
        HttpResponseMessage bad = await client.GetAsync("/api/v1/newssearch/vector?q=");
        HttpResponseMessage metricsOnPublic = await client.GetAsync("/metrics");

        await Assert.That(sources.GetProperty("data").EnumerateArray().Select(s => s.GetProperty("name").GetString()))
            .IsEquivalentTo(new[] { "bm25", "vector" });
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(bad.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(metricsOnPublic.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Http_NewsIdxDown_Is503_NeverA500()
    {
        await using WebApplicationFactory<Program> factory = Factory(new FakeHandler(_ => throw new HttpRequestException("refused")));
        HttpResponseMessage r = await factory.CreateClient().GetAsync("/api/v1/newssearch/vector?q=pike");

        await Assert.That(r.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(r.Headers.Contains("Retry-After")).IsTrue();
    }

    [Test]
    public async Task Http_NewsIdx500_Is503()
    {
        await using WebApplicationFactory<Program> factory = Factory(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        HttpResponseMessage r = await factory.CreateClient().GetAsync("/api/v1/newssearch/vector?q=pike");
        await Assert.That(r.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    private static WebApplicationFactory<Program> Factory(FakeHandler upstream) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("NEWSIDX_URL", "http://news-idx.test:4324");
            b.ConfigureTestServices(services =>
                services.Configure<HttpClientFactoryOptions>(NewsIdxSource.HttpClientName,
                    o => o.HttpMessageHandlerBuilderActions.Add(h => h.PrimaryHandler = upstream)));
        });

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(respond(request));
        }
    }
}
