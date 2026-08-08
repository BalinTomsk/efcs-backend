using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Registry;
using WeatherService.Configuration;

namespace WeatherService.Sources;

/// <summary>
/// Fetches raw forecast JSON from the Open-Meteo API.
/// </summary>
public class OpenMeteoFetcher : WeatherFetcherBase
{
    // Chrome-like User-Agent. The two "537.NN" WebKit/Safari build numbers are randomised daily (see
    // CurrentUserAgent); the {0}/{1} placeholders are filled per calendar day.
    private const string UserAgentTemplate =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.{0} "
        + "(KHTML, like Gecko) Chrome/124.0 Safari/537.{1}";

    private const int BuildMin = 11;
    private const int BuildMax = 97;

    public OpenMeteoFetcher(
        IHttpClientFactory httpClientFactory,
        IOptions<WorkerOptions> options,
        ResiliencePipelineProvider<string> pipelineProvider,
        ProviderRateLimiters rateLimiters,
        ILogger<OpenMeteoFetcher> logger)
        : base(ResiliencePipelines.OpenMeteo, httpClientFactory, options, pipelineProvider, rateLimiters, logger)
    {
    }

    protected override string ProviderName => "Open-Meteo";

    /// <summary>Fetches the hourly + daily forecast for a coordinate.</summary>
    public virtual Task<string> FetchAsync(double latitude, double longitude, CancellationToken ct = default)
    {
        string url = BuildUrl(latitude, longitude);
        var target = new FetchTarget(
            url,
            $"Open-Meteo feed not published for URL {url}",
            $" for URL {url}");

        return ExecuteFetchAsync(target, ct);
    }

    protected override void ConfigureRequest(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation("User-Agent", CurrentUserAgent());

    /// <summary>User-Agent for today, with WebKit/Safari build numbers randomised in [11, 97].</summary>
    internal static string CurrentUserAgent() => CurrentUserAgent(DateOnly.FromDateTime(DateTime.Now));

    /// <summary>
    /// Builds the User-Agent for a given day. The two build numbers are seeded by the calendar day, so
    /// they stay stable for a whole day and change from one day to the next. (The seed is the same day
    /// number the Java service uses; the generator differs, so the two emit different — equally valid —
    /// build numbers on the same date.)
    /// </summary>
    internal static string CurrentUserAgent(DateOnly date)
    {
        var random = new Random(date.DayNumber);
        int webKitBuild = random.Next(BuildMin, BuildMax + 1);
        int safariBuild = random.Next(BuildMin, BuildMax + 1);
        return string.Format(CultureInfo.InvariantCulture, UserAgentTemplate, webKitBuild, safariBuild);
    }

    private string BuildUrl(double latitude, double longitude) =>
        Options.OpenMeteoBaseUrl
        + "?latitude=" + latitude.ToString(CultureInfo.InvariantCulture)
        + "&longitude=" + longitude.ToString(CultureInfo.InvariantCulture)
        + "&hourly=temperature_2m,relative_humidity_2m,precipitation_probability,pressure_msl,"
        + "wind_speed_10m,wind_direction_10m,weather_code,rain"
        + "&daily=temperature_2m_max,temperature_2m_min&timezone=auto";
}
