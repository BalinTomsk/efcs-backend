using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TUnit.Core;
using WaterApi.Data;

namespace WaterApi.Tests;

/// <summary>
/// HTTP-level checks through the real pipeline (routing, envelope, exception mapping, ETag), with the
/// repository replaced by a fake. One host for the whole class — Serilog's bootstrap logger can only be
/// frozen once per process.
/// </summary>
public class StationControllerTests
{
    private static readonly FakeStationRepository Repo = CreateRepo();
    private static readonly Lazy<WebApplicationFactory<Program>> Factory = new(() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("DB_URL", "")
            .ConfigureTestServices(s =>
            {
                s.RemoveAll<IStationQueryRepository>();
                s.AddSingleton<IStationQueryRepository>(Repo);
            })));

    private static FakeStationRepository CreateRepo()
    {
        var repo = new FakeStationRepository();
        repo.Rows["US"] = [FakeStationRepository.Station(1, "US", "MN"), FakeStationRepository.Station(2, "US", "WI")];
        repo.Rows["CA"] = [FakeStationRepository.Station(10, "CA", "ON")];
        return repo;
    }

    // The API is bound to the public port only (RequireHost("*:8080")), so the client must say 8080. Built
    // once: tests run in parallel, and concurrent CreateClient calls would race to start a second host.
    private static readonly Lazy<HttpClient> SharedClient = new(() =>
        Factory.Value.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:8080") }));

    private static HttpClient Client() => SharedClient.Value;

    [Test]
    public async Task Map_ReturnsEnvelopeWithPins()
    {
        HttpResponseMessage response = await Client().GetAsync("/api/v1/water/station/map?country=us&state=mn");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement data = json.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("country").GetString()).IsEqualTo("US");
        await Assert.That(data.GetProperty("count").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("stations")[0].GetProperty("key").GetString()).IsEqualTo("1");
        await Assert.That(json.RootElement.GetProperty("error").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(json.RootElement.GetProperty("meta").GetProperty("stale").GetBoolean()).IsFalse();
        await Assert.That(response.Headers.ETag).IsNotNull();
    }

    [Test]
    public async Task Map_IfNoneMatch_Returns304()
    {
        HttpClient client = Client();
        HttpResponseMessage first = await client.GetAsync("/api/v1/water/station/map?country=CA");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/water/station/map?country=CA");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        HttpResponseMessage second = await client.SendAsync(request);

        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
    }

    [Test]
    public async Task Map_BadCountry_Is400Envelope()
    {
        HttpResponseMessage response = await Client().GetAsync("/api/v1/water/station/map?country=MX");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(json.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_request");
    }

    [Test]
    public async Task Station_BySid_And_Unknown()
    {
        HttpClient client = Client();

        HttpResponseMessage found = await client.GetAsync("/api/v1/water/station/10");
        HttpResponseMessage missing = await client.GetAsync("/api/v1/water/station/999");

        await Assert.That(found.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UnmappedPath_IsQuiet404Envelope()
    {
        HttpResponseMessage response = await Client().GetAsync("/wp-admin");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(json.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("not_found");
    }

    [Test]
    public async Task PublicApi_AnswersWhateverHostPortTheCallerUsed()
    {
        // Behind Docker's 8090->8080 mapping callers send "Host: <vpc-ip>:8090". The public surface must
        // not care: it is selected by the port the connection arrived on, never by the Host header
        // (0.1.0 used RequireHost("*:8080") and 404'd every request through the gateway).
        _ = SharedClient.Value; // host started once, before a second client is created
        HttpClient viaMappedPort =
            Factory.Value.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:8090") });

        HttpResponseMessage map = await viaMappedPort.GetAsync("/api/v1/water/station/map?country=CA");
        HttpResponseMessage health = await viaMappedPort.GetAsync("/health");

        await Assert.That(map.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Health_ReportsUp()
    {
        HttpResponseMessage response = await Client().GetAsync("/health");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(json.RootElement.GetProperty("status").GetString()).IsEqualTo("UP");
    }
}
