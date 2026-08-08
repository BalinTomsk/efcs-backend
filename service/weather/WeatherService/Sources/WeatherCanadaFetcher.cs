using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Registry;
using WeatherService.Configuration;

namespace WeatherService.Sources;

/// <summary>
/// Fetches raw SWOB real-time observation GeoJSON from Environment Canada's MSC GeoMet.
///
/// <para>SWOB has no per-station endpoint, so a station is located by searching a small bounding box
/// around its coordinate and taking the most recent observation inside it.</para>
/// </summary>
public partial class WeatherCanadaFetcher : WeatherFetcherBase
{
    public WeatherCanadaFetcher(
        IHttpClientFactory httpClientFactory,
        IOptions<WorkerOptions> options,
        ResiliencePipelineProvider<string> pipelineProvider,
        ProviderRateLimiters rateLimiters,
        ILogger<WeatherCanadaFetcher> logger)
        : base(ResiliencePipelines.WeatherCanada, httpClientFactory, options, pipelineProvider, rateLimiters, logger)
    {
    }

    protected override string ProviderName => "Weather Canada";

    /// <summary>Fetches the most recent SWOB observation near a coordinate.</summary>
    public virtual Task<string> FetchLatestObservationAsync(
        double latitude, double longitude, CancellationToken ct = default)
    {
        string url = BuildUrl(latitude, longitude);
        var target = new FetchTarget(
            url,
            $"Weather Canada observation not published for URL {url}",
            $" for URL {url}");

        return ExecuteFetchAsync(target, ct);
    }

    protected override void ConfigureRequest(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/geo+json");
        request.Headers.TryAddWithoutValidation("User-Agent", Options.WeatherCanadaUserAgent);
    }

    /// <summary>
    /// GeoMet answers "no station here" with a 200 and an empty feature collection rather than a 404, so
    /// that case is translated into the same skip a 404 would produce — it is a station without a nearby
    /// SWOB site, not an outage.
    /// </summary>
    protected override void ValidateBody(string body, FetchTarget target)
    {
        RequireJsonObjectShape(body, target);

        if (EmptyFeatures().IsMatch(body))
        {
            throw new FileNotFoundException($"Weather Canada returned no SWOB features for URL {target.Url}");
        }
    }

    private string BuildUrl(double latitude, double longitude)
    {
        double radius = Options.WeatherCanadaBboxRadiusDegrees;
        string bbox = string.Format(
            CultureInfo.InvariantCulture,
            "{0:F6},{1:F6},{2:F6},{3:F6}",
            longitude - radius, latitude - radius, longitude + radius, latitude + radius);

        return TrimBaseUrl(Options.WeatherCanadaBaseUrl)
            + "/collections/swob-realtime/items"
            + "?lang=en"
            + "&f=json"
            + "&limit=1"
            + "&sortby=-date_tm-value"
            + "&bbox=" + bbox;
    }

    /// <summary>Matches an empty <c>"features"</c> array regardless of the whitespace GeoMet emits.</summary>
    [GeneratedRegex("\"features\"\\s*:\\s*\\[\\s*\\]")]
    private static partial Regex EmptyFeatures();
}
