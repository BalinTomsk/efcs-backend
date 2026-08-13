using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using WeatherService.Canonical;
using WeatherService.Configuration;
using WeatherService.Domain;
using WeatherService.Processing;
using WeatherService.Sources;
using static WeatherService.Tests.TestSupport;

namespace WeatherService.Tests;

/// <summary>
/// Covers how a station's outcome is decided: the exception-to-outcome mapping every processor shares,
/// and — per provider — that a successful fetch is persisted and a failed one is not.
/// </summary>
public class StationProcessorTests
{
    private static readonly StationRef Subject = Station("MLI-1", 47.5, -122.3, "WA");

    [Test]
    public async Task SuccessfulWork_IsProcessed()
    {
        var processor = new TestProcessor();

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.Processed);
        await Assert.That(processor.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task UnpublishedFeed_IsSkippedNotFailed()
    {
        // Most stations have no feed with most providers; counting those as failures would make every
        // cycle look degraded and permanently suppress post-processing.
        var processor = new TestProcessor { ToThrow = new FileNotFoundException("no feed") };

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.Skipped);
    }

    [Test]
    public async Task ServiceUnavailable_IsReportedAsItsOwnOutcome()
    {
        var processor = new TestProcessor
        {
            ToThrow = new HttpRequestException("boom", null, HttpStatusCode.ServiceUnavailable),
        };

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.FailedHttp503);
    }

    [Test]
    public async Task ServiceUnavailable_IsAlsoDetectedFromTheMessage()
    {
        // Not every path carries a status code — the fetchers put "HTTP 503" in the text as well.
        var processor = new TestProcessor { ToThrow = new IOException("Provider returned HTTP 503") };

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.FailedHttp503);
    }

    [Test]
    public async Task AnyOtherError_IsFailedAndNeverPropagates()
    {
        var processor = new TestProcessor { ToThrow = new InvalidOperationException("unexpected") };

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.Failed);
    }

    [Test]
    public async Task StartupVerification_ReportsTheSameOutcomesAsProcessing()
    {
        var processor = new TestProcessor { ToThrow = new FileNotFoundException("no feed") };

        await Assert.That(await processor.VerifyStartupAsync(Subject, "US")).IsEqualTo(ProcessingOutcome.Skipped);
    }

    [Test]
    public async Task OpenMeteo_PersistsTheCanonicalEnvelopeNotTheRawPayload()
    {
        var repository = new FakeWeatherDataRepository();
        var processor = new StationProcessorOpen(
            OpenMeteo(OpenMeteoDocument), repository, new OpenMeteoConverter(),
            NullLogger<StationProcessorOpen>.Instance);

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.Processed);
        await Assert.That(repository.Saved).HasCount().EqualTo(1);

        var (mli, json, sourceType) = repository.Saved[0];
        await Assert.That(mli).IsEqualTo("MLI-1");
        await Assert.That(sourceType).IsEqualTo(WeatherSourceType.OpenMeteo);

        JsonNode envelope = JsonNode.Parse(json)!;
        await Assert.That(envelope["schema"]!.GetValue<string>()).IsEqualTo("fishfind.weather.forecast/v1");
        await Assert.That(envelope["days"]!.AsArray().Count).IsEqualTo(1);
        // the provider's own document is still in there, so the payload stays replayable
        await Assert.That(envelope["raw"]!["timezone"]!.GetValue<string>()).IsEqualTo("America/Los_Angeles");
    }

    [Test]
    public async Task OpenMeteo_FailsTheStationWhenTheProviderChangesShape()
    {
        // The old T-SQL parser wrote nothing and reported success for this; it is now a counted failure.
        var repository = new FakeWeatherDataRepository();
        var processor = new StationProcessorOpen(
            OpenMeteo("""{"x":1}"""), repository, new OpenMeteoConverter(),
            NullLogger<StationProcessorOpen>.Instance);

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.Failed);
        await Assert.That(repository.Saved).IsEmpty();
    }

    [Test]
    public async Task OpenMeteo_PersistsNothingWhenTheFetchFails()
    {
        var repository = new FakeWeatherDataRepository();
        var processor = new StationProcessorOpen(
            OpenMeteo(HttpStatusCode.InternalServerError), repository, new OpenMeteoConverter(),
            NullLogger<StationProcessorOpen>.Instance);

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.Failed);
        await Assert.That(repository.Saved).IsEmpty();
    }

    [Test]
    public async Task WeatherGov_PersistsTheCanonicalEnvelopeUnderTheGaugeMli()
    {
        // Two calls now: /points to find the grid cell's forecast URL, then the forecast itself.
        var repository = new FakeWeatherDataRepository();
        var processor = new StationProcessorWeatherGov(
            WeatherGovForecast(), StubResolver("KNYC"), repository, new WeatherGovConverter(),
            NullLogger<StationProcessorWeatherGov>.Instance);

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.Processed);
        await Assert.That(repository.Saved).HasCount().EqualTo(1);

        var (mli, json, sourceType) = repository.Saved[0];
        // persisted under the WATER gauge's mli, not the NWS grid cell
        await Assert.That(mli).IsEqualTo("MLI-1");
        await Assert.That(sourceType).IsEqualTo(WeatherSourceType.WeatherGov);

        JsonNode envelope = JsonNode.Parse(json)!;
        await Assert.That(envelope["provider"]!.GetValue<string>()).IsEqualTo("weather-gov");
        await Assert.That(envelope["days"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(envelope["days"]![0]!["tempHighC"]!.GetValue<double>()).IsEqualTo(29.0);
    }

    [Test]
    public async Task WeatherGov_FailsTheStationWhenGivenAnObservationInsteadOfAForecast()
    {
        // What this provider used to store. It must fail loudly rather than be written and then
        // silently ignored by the database.
        var repository = new FakeWeatherDataRepository();
        var processor = new StationProcessorWeatherGov(
            WeatherGovForecast("""{"properties":{"temperature":{"value":21.1}}}"""), StubResolver("KNYC"),
            repository, new WeatherGovConverter(), NullLogger<StationProcessorWeatherGov>.Instance);

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.Failed);
        await Assert.That(repository.Saved).IsEmpty();
    }

    [Test]
    public async Task WeatherGov_SkipsWhenTheCoordinateIsOutsideNwsCoverage()
    {
        // /points answers without a forecast URL — no grid cell here, which is a permanent answer and
        // a skip, not a failure.
        var repository = new FakeWeatherDataRepository();
        var processor = new StationProcessorWeatherGov(
            Fetcher(new StubHandler(_ => Ok("""{"properties":{}}""")),
                (f, o, p, l) => new WeatherGovFetcher(f, o, p, l, NullLogger<WeatherGovFetcher>.Instance)),
            StubResolver("KNYC"), repository, new WeatherGovConverter(),
            NullLogger<StationProcessorWeatherGov>.Instance);

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.Skipped);
        await Assert.That(repository.Saved).IsEmpty();
    }

    /// <summary>
    /// The gridpoint path makes TWO calls — <c>/points</c> for the grid cell's forecast URL, then that
    /// URL — so the stub has to answer differently depending on which one it sees.
    /// </summary>
    private static WeatherGovFetcher WeatherGovForecast(string? forecastBody = null) =>
        Fetcher(
            new StubHandler(request => request.RequestUri!.AbsolutePath.Contains("/points/")
                ? Ok("""{"properties":{"forecast":"https://api.weather.gov/gridpoints/BOI/62,61/forecast"}}""")
                : Ok(forecastBody ?? GridpointForecastDocument)),
            (f, o, p, l) => new WeatherGovFetcher(f, o, p, l, NullLogger<WeatherGovFetcher>.Instance));

    /// <summary>A real NWS gridpoint forecast: periods of half a day, flagged isDaytime, in si units.</summary>
    private const string GridpointForecastDocument = """
        {"properties":{"units":"si","periods":[
          {"number":1,"name":"Today","startTime":"2026-08-13T06:00:00-06:00","isDaytime":true,
           "temperature":29,"temperatureUnit":"C","probabilityOfPrecipitation":{"value":20},
           "relativeHumidity":{"value":41},"windSpeed":"10 to 15 km/h","windDirection":"SSW",
           "icon":"https://api.weather.gov/icons/land/day/sct?size=medium",
           "shortForecast":"Mostly Sunny","detailedForecast":"Mostly sunny."}]}}
        """;

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Test]
    public async Task WeatherCanada_PersistsTheFetchedPayload()
    {
        var repository = new FakeWeatherDataRepository();
        var processor = new StationProcessorWeatherCanada(
            WeatherCanada("""{"features":[{"id":"1"}]}"""), repository,
            NullLogger<StationProcessorWeatherCanada>.Instance);

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.Processed);
        await Assert.That(repository.Saved).HasCount().EqualTo(1);
    }

    [Test]
    public async Task VisualCrossing_PersistsTheCanonicalEnvelopeStampedWithItsProvider()
    {
        var repository = new FakeWeatherDataRepository();
        var processor = new StationProcessorVisualCrossing(
            VisualCrossing(VisualCrossingDocument), repository, new VisualCrossingConverter(),
            NullLogger<StationProcessorVisualCrossing>.Instance);

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.Processed);
        await Assert.That(repository.Saved).HasCount().EqualTo(1);

        var (mli, json, sourceType) = repository.Saved[0];
        await Assert.That(mli).IsEqualTo("MLI-1");
        await Assert.That(sourceType).IsEqualTo(WeatherSourceType.VisualCrossing);
        await Assert.That(JsonNode.Parse(json)!["provider"]!.GetValue<string>()).IsEqualTo("visual-crossing");
    }

    /// <summary>A real Open-Meteo response, dated relative to now so the converter always accepts it.</summary>
    private static string OpenMeteoDocument =>
        $$"""
          {"hourly":{"time":["{{DateTime.UtcNow:yyyy-MM-dd}}T23:00"],
             "temperature_2m":[16.0],"rain":[0.0],"weather_code":[0]},
           "daily":{"time":["{{DateTime.UtcNow:yyyy-MM-dd}}"],
             "temperature_2m_max":[24.7],"temperature_2m_min":[11.8]},
           "timezone":"America/Los_Angeles"}
          """;

    /// <summary>A real Visual Crossing response, dated today so it survives the today..today+6 clip.</summary>
    private static string VisualCrossingDocument =>
        $$"""
          {"queryCost":1,"timezone":"America/Boise","days":[
            {"datetime":"{{DateTime.UtcNow:yyyy-MM-dd}}","tempmax":85.0,"tempmin":62.0,"temp":74.1,
             "humidity":39.7,"precip":0.0,"precipprob":6.0,"windspeed":12.8,"winddir":201.2,
             "pressure":1009.7,"conditions":"Partially cloudy","description":"Partly cloudy.",
             "icon":"partly-cloudy-day"}]}
          """;

    [Test]
    public async Task GoogleWeather_PersistsTheFetchedPayload()
    {
        var repository = new FakeWeatherDataRepository();
        var processor = new StationProcessorGoogleWeather(
            GoogleWeather("""{"gw":1}"""), repository, NullLogger<StationProcessorGoogleWeather>.Instance);

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.Processed);
        await Assert.That(repository.Saved)
            .IsEquivalentTo(new[] { ("MLI-1", """{"gw":1}""", WeatherSourceType.GoogleWeather) });
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static OpenMeteoFetcher OpenMeteo(string body) =>
        Fetcher(Responding(body), (f, o, p, l) => new OpenMeteoFetcher(f, o, p, l, NullLogger<OpenMeteoFetcher>.Instance));

    private static OpenMeteoFetcher OpenMeteo(HttpStatusCode status) =>
        Fetcher(Responding(status), (f, o, p, l) => new OpenMeteoFetcher(f, o, p, l, NullLogger<OpenMeteoFetcher>.Instance));

    private static WeatherGovFetcher WeatherGov(string body) =>
        Fetcher(Responding(body), (f, o, p, l) => new WeatherGovFetcher(f, o, p, l, NullLogger<WeatherGovFetcher>.Instance));

    private static WeatherCanadaFetcher WeatherCanada(string body) =>
        Fetcher(Responding(body), (f, o, p, l) => new WeatherCanadaFetcher(f, o, p, l, NullLogger<WeatherCanadaFetcher>.Instance));

    private static VisualCrossingFetcher VisualCrossing(string body) =>
        Fetcher(Responding(body), (f, o, p, l) => new VisualCrossingFetcher(f, o, p, l, NullLogger<VisualCrossingFetcher>.Instance),
            options => options.VisualCrossingApiKey = "test-key");

    private static GoogleWeatherFetcher GoogleWeather(string body) =>
        Fetcher(Responding(body), (f, o, p, l) => new GoogleWeatherFetcher(f, o, p, l, NullLogger<GoogleWeatherFetcher>.Instance),
            options => options.GoogleWeatherApiKey = "test-key");

    private static StubHandler Responding(string body) => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    });

    private static StubHandler Responding(HttpStatusCode status) => new(_ => new HttpResponseMessage(status));

    /// <summary>A resolver that answers with a fixed NWS station and never touches the network or DB.</summary>
    private static WeatherGovStationResolver StubResolver(string? nwsStation) =>
        new FixedResolver(nwsStation);

    private sealed class FixedResolver(string? nwsStation) : WeatherGovStationResolver(
        null!, null!, NullLogger<WeatherGovStationResolver>.Instance)
    {
        public override Task<string?> ResolveAsync(StationRef station, CancellationToken ct = default) =>
            Task.FromResult(nwsStation);
    }

    /// <summary>A processor whose only job is to throw whatever the test wants classified.</summary>
    private sealed class TestProcessor : StationProcessorBase
    {
        public Exception? ToThrow { get; init; }

        public int Calls { get; private set; }

        protected override Task ProcessStationAsync(StationRef station, CancellationToken ct)
        {
            Calls++;
            return ToThrow is null ? Task.CompletedTask : Task.FromException(ToThrow);
        }

        protected override ILogger Logger => NullLogger.Instance;

        protected override string Country => "US";

        protected override string MissingSourceDescription => "test source";
    }
}
