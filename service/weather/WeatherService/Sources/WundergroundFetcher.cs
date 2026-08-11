using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Registry;
using WeatherService.Configuration;

namespace WeatherService.Sources;

/// <summary>
/// Fetches current conditions from Weather Underground (IBM/The Weather Company PWS API).
///
/// <para>A PWS Contributor key has no lat/lon forecast endpoint, so each station costs two calls:
/// <c>v3/location/near</c> resolves the nearest personal weather station to the water station's
/// coordinates, then <c>v2/pws/observations/current</c> fetches that station's latest reading. Both
/// run through the same rate limiter/circuit breaker/retry pipeline as independent requests, so the
/// effective call volume against the Wunderground quota is roughly double the configured daily
/// station limit.</para>
/// </summary>
public class WundergroundFetcher : WeatherFetcherBase
{
    public WundergroundFetcher(
        IHttpClientFactory httpClientFactory,
        IOptions<WorkerOptions> options,
        ResiliencePipelineProvider<string> pipelineProvider,
        ProviderRateLimiters rateLimiters,
        ILogger<WundergroundFetcher> logger)
        : base(ResiliencePipelines.Wunderground, httpClientFactory, options, pipelineProvider, rateLimiters, logger)
    {
    }

    protected override string ProviderName => "Wunderground";

    /// <summary>Fetches current conditions for a coordinate via its nearest Wunderground PWS station.</summary>
    public virtual async Task<string> FetchCurrentAsync(double latitude, double longitude, CancellationToken ct = default)
    {
        string stationId = await NearestStationIdAsync(latitude, longitude, ct).ConfigureAwait(false);

        var target = new FetchTarget(
            BuildObservationUrl(stationId),
            $"Wunderground observation not found for station {stationId}",
            Suffix: string.Empty);

        return await ExecuteFetchAsync(target, ct).ConfigureAwait(false);
    }

    private async Task<string> NearestStationIdAsync(double latitude, double longitude, CancellationToken ct)
    {
        string latText = latitude.ToString(CultureInfo.InvariantCulture);
        string lonText = longitude.ToString(CultureInfo.InvariantCulture);

        var target = new FetchTarget(
            BuildLocationUrl(latitude, longitude),
            $"Wunderground nearest-station lookup not found for latitude={latText} longitude={lonText}",
            Suffix: string.Empty);

        string json = await ExecuteFetchAsync(target, ct).ConfigureAwait(false);
        string? stationId = ExtractFirstStationId(json);
        if (stationId is null)
        {
            throw new FileNotFoundException(
                $"No Wunderground PWS station found near latitude={latText} longitude={lonText}");
        }

        return stationId;
    }

    /// <summary>Reads <c>location.stationId[0]</c> from the <c>v3/location/near</c> response.</summary>
    private static string? ExtractFirstStationId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("location", out JsonElement location)
                && location.TryGetProperty("stationId", out JsonElement stationIds)
                && stationIds.ValueKind == JsonValueKind.Array
                && stationIds.GetArrayLength() > 0)
            {
                string? first = stationIds[0].GetString();
                return string.IsNullOrWhiteSpace(first) ? null : first;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    protected override void ConfigureRequest(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

    /// <summary>
    /// A rejected key is called out separately from a generic non-200: it is a configuration fault that
    /// will fail identically for every station until someone fixes the key.
    /// </summary>
    protected override void ValidateStatus(HttpResponseMessage response, FetchTarget target)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new HttpRequestException(
                $"Wunderground authentication failed with HTTP {(int)response.StatusCode}",
                inner: null,
                statusCode: response.StatusCode);
        }
    }

    private string BuildLocationUrl(double latitude, double longitude)
    {
        string key = RequireApiKey();

        return TrimBaseUrl(Options.WundergroundLocationBaseUrl)
            + "?geocode=" + latitude.ToString(CultureInfo.InvariantCulture)
            + "," + longitude.ToString(CultureInfo.InvariantCulture)
            + "&product=pws"
            + "&format=json"
            + "&apiKey=" + Uri.EscapeDataString(key);
    }

    private string BuildObservationUrl(string stationId)
    {
        string key = RequireApiKey();

        return TrimBaseUrl(Options.WundergroundObservationBaseUrl)
            + "?stationId=" + Uri.EscapeDataString(stationId)
            + "&format=json"
            + "&units=e"
            + "&apiKey=" + Uri.EscapeDataString(key);
    }

    private string RequireApiKey()
    {
        if (string.IsNullOrWhiteSpace(Options.WundergroundApiKey))
        {
            throw new IOException("WUNDERGROUND_API_KEY is not configured");
        }
        return Options.WundergroundApiKey.Trim();
    }
}
