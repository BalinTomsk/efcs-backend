using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Registry;
using WeatherService.Configuration;

namespace WeatherService.Sources;

/// <summary>
/// Fetches raw current-conditions JSON from the Google Maps Platform Weather API.
/// </summary>
public class GoogleWeatherFetcher : WeatherFetcherBase
{
    public GoogleWeatherFetcher(
        IHttpClientFactory httpClientFactory,
        IOptions<WorkerOptions> options,
        ResiliencePipelineProvider<string> pipelineProvider,
        ProviderRateLimiters rateLimiters,
        ILogger<GoogleWeatherFetcher> logger)
        : base(ResiliencePipelines.GoogleWeather, httpClientFactory, options, pipelineProvider, rateLimiters, logger)
    {
    }

    protected override string ProviderName => "Google Weather";

    /// <summary>Fetches current conditions for a coordinate.</summary>
    public virtual Task<string> FetchCurrentAsync(double latitude, double longitude, CancellationToken ct = default)
    {
        var target = new FetchTarget(
            BuildUrl(latitude, longitude),
            "Google Weather feed not published for latitude="
            + latitude.ToString(CultureInfo.InvariantCulture)
            + " longitude=" + longitude.ToString(CultureInfo.InvariantCulture),
            Suffix: string.Empty);

        return ExecuteFetchAsync(target, ct);
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
                $"Google Weather authentication failed with HTTP {(int)response.StatusCode}",
                inner: null,
                statusCode: response.StatusCode);
        }
    }

    private string BuildUrl(double latitude, double longitude)
    {
        string key = RequireApiKey();

        return TrimBaseUrl(Options.GoogleWeatherBaseUrl)
            + "?location.latitude=" + latitude.ToString(CultureInfo.InvariantCulture)
            + "&location.longitude=" + longitude.ToString(CultureInfo.InvariantCulture)
            + "&unitsSystem=IMPERIAL"
            + "&key=" + Uri.EscapeDataString(key);
    }

    private string RequireApiKey()
    {
        if (string.IsNullOrWhiteSpace(Options.GoogleWeatherApiKey))
        {
            throw new IOException("GOOGLE_WEATHER_API_KEY is not configured");
        }
        return Options.GoogleWeatherApiKey.Trim();
    }
}
