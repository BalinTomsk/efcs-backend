using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using WeatherService.Configuration;
using WeatherService.Sources;
using static WeatherService.Tests.TestSupport;

namespace WeatherService.Tests;

/// <summary>
/// Covers the transport shared by all five providers — how a response is classified into "store it",
/// "skip this station", or "fail this station" — using <see cref="OpenMeteoFetcher"/> as the stand-in.
/// This is the decision that determines whether a station is retried, skipped, or burned for the cycle.
/// </summary>
public class WeatherFetcherTransportTests
{
    [Test]
    public async Task SuccessfulResponse_ReturnsBodyVerbatim()
    {
        // The payload is persisted raw, so nothing may rewrite it — not even the escaped quote below.
        const string body = """{"raw":"va\"lue"}""";
        OpenMeteoFetcher fetcher = NewFetcher(Ok(body));

        await Assert.That(await fetcher.FetchAsync(47.5, -122.3)).IsEqualTo(body);
    }

    [Test]
    public async Task NotFound_IsSkippedNotFailed()
    {
        // 404 means the feed is not published for this coordinate: skipped, never retried.
        OpenMeteoFetcher fetcher = NewFetcher(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        await Assert.That(() => fetcher.FetchAsync(47.5, -122.3)).Throws<FileNotFoundException>();
    }

    [Test]
    public async Task ServerError_FailsWithTheStatusAttached()
    {
        OpenMeteoFetcher fetcher = NewFetcher(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        HttpRequestException thrown = await CaptureAsync<HttpRequestException>(() => fetcher.FetchAsync(47.5, -122.3));

        await Assert.That(thrown.Message).Contains("Open-Meteo returned HTTP 500");
        await Assert.That(thrown.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task ServiceUnavailable_CarriesTheMarkerTheProcessorLooksFor()
    {
        // StationProcessorBase reports 503 distinctly, so the message and status must both say so.
        OpenMeteoFetcher fetcher = NewFetcher(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        HttpRequestException thrown = await CaptureAsync<HttpRequestException>(() => fetcher.FetchAsync(47.5, -122.3));

        await Assert.That(thrown.Message).Contains("HTTP 503");
        await Assert.That(thrown.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task RateLimited_HonoursRetryAfterThenSucceeds()
    {
        int calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            if (calls > 1)
            {
                return JsonResponse("""{"ok":true}""");
            }

            var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            throttled.Headers.TryAddWithoutValidation("Retry-After", "0");
            return throttled;
        });

        OpenMeteoFetcher fetcher = NewFetcher(handler);

        await Assert.That(await fetcher.FetchAsync(47.5, -122.3)).IsEqualTo("""{"ok":true}""");
        await Assert.That(handler.RequestCount).IsEqualTo(2);
    }

    [Test]
    public async Task RateLimited_Persistently_FailsAfterTheConfiguredWaits()
    {
        // The Retry-After waits have already been honoured inline, so this exception is excluded from
        // retry — another round of backoff would only burn the cycle.
        var handler = new StubHandler(_ =>
        {
            var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            throttled.Headers.TryAddWithoutValidation("Retry-After", "0");
            return throttled;
        });

        OpenMeteoFetcher fetcher = NewFetcher(handler);

        RateLimitedException thrown = await CaptureAsync<RateLimitedException>(() => fetcher.FetchAsync(47.5, -122.3));

        await Assert.That(thrown.Message).Contains("rate limited (429) after 2 waits");
        // 1 initial + 2 honoured waits = 3 attempts, and the count is the number of WAITS, not requests.
        await Assert.That(handler.RequestCount).IsEqualTo(3);
    }

    [Test]
    public async Task NonJsonBodyWith200_IsRejected()
    {
        // A captive portal or HTML error page returned with a 200 must never reach the database.
        OpenMeteoFetcher fetcher = NewFetcher(Ok("<!doctype html><html><body>captive portal</body></html>"));

        IOException thrown = await CaptureAsync<IOException>(() => fetcher.FetchAsync(47.5, -122.3));

        await Assert.That(thrown.Message).Contains("non-JSON body");
    }

    [Test]
    public async Task OversizedBody_IsRejectedRatherThanStored()
    {
        // The body is written verbatim into a column, so an unbounded read is an unbounded INSERT.
        OpenMeteoFetcher fetcher = NewFetcher(
            Ok("{\"pad\":\"" + new string('x', 500) + "\"}"),
            options => options.MaxResponseBytes = 64);

        IOException thrown = await CaptureAsync<IOException>(() => fetcher.FetchAsync(47.5, -122.3));

        await Assert.That(thrown.Message).Contains("exceeded 64 bytes");
    }

    [Test]
    public async Task Request_CarriesTheCoordinatesAndADailyUserAgent()
    {
        var handler = Ok("""{"ok":true}""");
        OpenMeteoFetcher fetcher = NewFetcher(handler);

        await fetcher.FetchAsync(47.5, -122.3);

        string url = handler.LastRequest!.RequestUri!.ToString();
        await Assert.That(url).Contains("latitude=47.5");
        await Assert.That(url).Contains("longitude=-122.3");
        await Assert.That(url).Contains("hourly=temperature_2m");
        await Assert.That(handler.LastRequest.Headers.GetValues("User-Agent").First()).StartsWith("Mozilla/5.0");
    }

    [Test]
    public async Task UserAgent_IsStablePerDayAndChangesBetweenDays()
    {
        var day = new DateOnly(2026, 7, 15);
        var nextDay = new DateOnly(2026, 7, 16);

        await Assert.That(OpenMeteoFetcher.CurrentUserAgent(day))
            .IsEqualTo(OpenMeteoFetcher.CurrentUserAgent(day));
        await Assert.That(OpenMeteoFetcher.CurrentUserAgent(day))
            .IsNotEqualTo(OpenMeteoFetcher.CurrentUserAgent(nextDay));
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static OpenMeteoFetcher NewFetcher(HttpMessageHandler handler, Action<WorkerOptions>? customise = null) =>
        Fetcher(handler, (factory, options, pipelines, limiters) =>
            new OpenMeteoFetcher(factory, options, pipelines, limiters, NullLogger<OpenMeteoFetcher>.Instance),
            customise);

    private static StubHandler Ok(string body) => new(_ => JsonResponse(body));

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<T> CaptureAsync<T>(Func<Task> action) where T : Exception
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
