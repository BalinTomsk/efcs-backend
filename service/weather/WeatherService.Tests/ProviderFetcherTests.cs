using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using WeatherService.Configuration;
using WeatherService.Sources;
using static WeatherService.Tests.TestSupport;

namespace WeatherService.Tests;

/// <summary>
/// Covers what each provider does differently from the shared transport: the URL it builds, the
/// headers it must send, and the failure modes unique to it. The common request/response handling is
/// covered once in <see cref="WeatherFetcherTransportTests"/>.
/// </summary>
public class ProviderFetcherTests
{
    [Test]
    public async Task WeatherGov_UppercasesTheStationAndAsksForGeoJson()
    {
        var handler = Ok("""{"ok":true}""");
        WeatherGovFetcher fetcher = WeatherGov(handler);

        await fetcher.FetchLatestObservationAsync("  knyc  ");

        await Assert.That(handler.LastRequest!.RequestUri!.ToString())
            .IsEqualTo("https://api.weather.gov/stations/KNYC/observations/latest");
        await Assert.That(handler.LastRequest.Headers.GetValues("Accept").First()).IsEqualTo("application/geo+json");
        // Weather.gov rejects anonymous clients, so the identifying User-Agent is not optional.
        await Assert.That(handler.LastRequest.Headers.GetValues("User-Agent").First()).IsNotEmpty();
    }

    [Test]
    public async Task WeatherGov_BlankStationIsARejectedArgumentNotARequest()
    {
        var handler = Ok("""{"ok":true}""");
        WeatherGovFetcher fetcher = WeatherGov(handler);

        await Assert.That(() => fetcher.FetchLatestObservationAsync(" ")).Throws<ArgumentException>();
        await Assert.That(handler.RequestCount).IsZero();
    }

    [Test]
    public async Task WeatherCanada_EmptyFeatureCollectionIsASkip()
    {
        // GeoMet answers "no station near here" with a 200 and an empty collection, not a 404. Treating
        // it as a failure would mark thousands of inland stations as broken every cycle.
        WeatherCanadaFetcher fetcher = WeatherCanada(Ok("""{"type":"FeatureCollection","features": [ ]}"""));

        await Assert.That(() => fetcher.FetchLatestObservationAsync(43.6, -79.4)).Throws<FileNotFoundException>();
    }

    [Test]
    public async Task WeatherCanada_PopulatedFeatureCollectionIsReturned()
    {
        const string body = """{"type":"FeatureCollection","features":[{"id":"1"}]}""";
        WeatherCanadaFetcher fetcher = WeatherCanada(Ok(body));

        await Assert.That(await fetcher.FetchLatestObservationAsync(43.6, -79.4)).IsEqualTo(body);
    }

    [Test]
    public async Task WeatherCanada_SearchesABoxAroundTheStation()
    {
        var handler = Ok("""{"features":[{"id":"1"}]}""");
        WeatherCanadaFetcher fetcher = WeatherCanada(handler, options => options.WeatherCanadaBboxRadiusDegrees = 0.05);

        await fetcher.FetchLatestObservationAsync(43.60, -79.40);

        string url = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.ToString());
        await Assert.That(url).Contains("/collections/swob-realtime/items");
        await Assert.That(url).Contains("bbox=-79.450000,43.550000,-79.350000,43.650000");
        await Assert.That(url).Contains("sortby=-date_tm-value");
    }

    [Test]
    public async Task VisualCrossing_MissingKeyFailsBeforeAnyRequest()
    {
        var handler = Ok("""{"ok":true}""");
        VisualCrossingFetcher fetcher = VisualCrossing(handler, options => options.VisualCrossingApiKey = "");

        await Assert.That(() => fetcher.FetchCurrentAsync(47.5, -122.3)).Throws<IOException>();
        await Assert.That(handler.RequestCount).IsZero();
    }

    [Test]
    public async Task VisualCrossing_RejectedKeyIsCalledOutSeparately()
    {
        // A 401/403 is a configuration fault that will fail identically for every station, so it reads
        // differently in the logs from a station that merely has no data.
        VisualCrossingFetcher fetcher = VisualCrossing(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)),
            options => options.VisualCrossingApiKey = "secret");

        HttpRequestException thrown = await Capture<HttpRequestException>(() => fetcher.FetchCurrentAsync(47.5, -122.3));

        await Assert.That(thrown.Message).Contains("authentication failed with HTTP 401");
    }

    [Test]
    public async Task VisualCrossing_BuildsTheTimelineUrlWithTheCoordinatePair()
    {
        var handler = Ok("""{"ok":true}""");
        VisualCrossingFetcher fetcher = VisualCrossing(handler, options => options.VisualCrossingApiKey = "k e y");

        await fetcher.FetchCurrentAsync(47.5, -122.3);

        // AbsoluteUri, not ToString(): ToString() un-escapes for display, which would hide the escaping.
        string url = handler.LastRequest!.RequestUri!.AbsoluteUri;
        await Assert.That(url).Contains("/47.5%2C-122.3?");
        await Assert.That(url).Contains("include=current");
        // The key is a secret with no guaranteed charset — it must be escaped, not pasted in raw.
        await Assert.That(url).Contains("key=k%20e%20y");
    }

    [Test]
    public async Task GoogleWeather_MissingKeyFailsBeforeAnyRequest()
    {
        var handler = Ok("""{"ok":true}""");
        GoogleWeatherFetcher fetcher = GoogleWeather(handler, options => options.GoogleWeatherApiKey = "");

        await Assert.That(() => fetcher.FetchCurrentAsync(47.5, -122.3)).Throws<IOException>();
        await Assert.That(handler.RequestCount).IsZero();
    }

    [Test]
    public async Task GoogleWeather_BuildsTheLookupUrlWithImperialUnits()
    {
        var handler = Ok("""{"ok":true}""");
        GoogleWeatherFetcher fetcher = GoogleWeather(handler, options => options.GoogleWeatherApiKey = "abc");

        await fetcher.FetchCurrentAsync(47.5, -122.3);

        string url = handler.LastRequest!.RequestUri!.ToString();
        await Assert.That(url).StartsWith("https://weather.googleapis.com/v1/currentConditions:lookup?");
        await Assert.That(url).Contains("location.latitude=47.5");
        await Assert.That(url).Contains("location.longitude=-122.3");
        await Assert.That(url).Contains("unitsSystem=IMPERIAL");
    }

    [Test]
    public async Task Wunderground_MissingKeyFailsBeforeAnyRequest()
    {
        var handler = Ok("""{"ok":true}""");
        WundergroundFetcher fetcher = Wunderground(handler, options => options.WundergroundApiKey = "");

        await Assert.That(() => fetcher.FetchCurrentAsync(47.5, -122.3)).Throws<IOException>();
        await Assert.That(handler.RequestCount).IsZero();
    }

    [Test]
    public async Task Wunderground_ResolvesNearestStationThenFetchesItsObservation()
    {
        const string observation = """{"observations":[{"stationID":"KTESTPWS1"}]}""";
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains("location")
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"location":{"stationId":["KTESTPWS1","KTESTPWS2"]}}""", Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(observation, Encoding.UTF8, "application/json"),
            });
        WundergroundFetcher fetcher = Wunderground(handler, options => options.WundergroundApiKey = "abc");

        string result = await fetcher.FetchCurrentAsync(40.7128, -74.0060);

        await Assert.That(result).IsEqualTo(observation);
        await Assert.That(handler.RequestCount).IsEqualTo(2);
    }

    [Test]
    public async Task Wunderground_NoNearbyStationIsASkip()
    {
        // The location lookup succeeding with an empty stationId array means "nothing nearby" -- a
        // normal skip for this water station, not a failure, and the observation call never happens.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"location":{"stationId":[]}}""", Encoding.UTF8, "application/json"),
        });
        WundergroundFetcher fetcher = Wunderground(handler, options => options.WundergroundApiKey = "abc");

        await Assert.That(() => fetcher.FetchCurrentAsync(1.0, 2.0)).Throws<FileNotFoundException>();
        await Assert.That(handler.RequestCount).IsEqualTo(1);
    }

    [Test]
    public async Task Wunderground_LocationLookupUsesGeocodeAndPwsProduct()
    {
        var requests = new List<Uri>();
        var recorder = new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.AbsolutePath.Contains("location")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"location":{"stationId":["KTESTPWS1"]}}""", Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"observations":[]}""", Encoding.UTF8, "application/json"),
                };
        });
        WundergroundFetcher fetcher = Wunderground(recorder, options => options.WundergroundApiKey = "abc");

        await fetcher.FetchCurrentAsync(40.7128, -74.0060);

        string locationUrl = requests[0].ToString();
        await Assert.That(locationUrl).Contains("geocode=40.7128,-74.006");
        await Assert.That(locationUrl).Contains("product=pws");
        string observationUrl = requests[1].ToString();
        await Assert.That(observationUrl).Contains("stationId=KTESTPWS1");
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static WeatherGovFetcher WeatherGov(HttpMessageHandler handler, Action<WorkerOptions>? customise = null) =>
        Fetcher(handler, (factory, options, pipelines, limiters) =>
            new WeatherGovFetcher(factory, options, pipelines, limiters, NullLogger<WeatherGovFetcher>.Instance),
            customise);

    private static WeatherCanadaFetcher WeatherCanada(HttpMessageHandler handler, Action<WorkerOptions>? customise = null) =>
        Fetcher(handler, (factory, options, pipelines, limiters) =>
            new WeatherCanadaFetcher(factory, options, pipelines, limiters, NullLogger<WeatherCanadaFetcher>.Instance),
            customise);

    private static VisualCrossingFetcher VisualCrossing(HttpMessageHandler handler, Action<WorkerOptions>? customise = null) =>
        Fetcher(handler, (factory, options, pipelines, limiters) =>
            new VisualCrossingFetcher(factory, options, pipelines, limiters, NullLogger<VisualCrossingFetcher>.Instance),
            customise);

    private static GoogleWeatherFetcher GoogleWeather(HttpMessageHandler handler, Action<WorkerOptions>? customise = null) =>
        Fetcher(handler, (factory, options, pipelines, limiters) =>
            new GoogleWeatherFetcher(factory, options, pipelines, limiters, NullLogger<GoogleWeatherFetcher>.Instance),
            customise);

    private static WundergroundFetcher Wunderground(HttpMessageHandler handler, Action<WorkerOptions>? customise = null) =>
        Fetcher(handler, (factory, options, pipelines, limiters) =>
            new WundergroundFetcher(factory, options, pipelines, limiters, NullLogger<WundergroundFetcher>.Instance),
            customise);

    private static StubHandler Ok(string body) => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    });

    private static async Task<T> Capture<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T ex)
        {
            return ex;
        }

        throw new InvalidOperationException($"expected a {typeof(T).Name}, but none was thrown");
    }
}
