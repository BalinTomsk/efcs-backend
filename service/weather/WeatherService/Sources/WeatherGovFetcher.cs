using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Registry;
using WeatherService.Configuration;

namespace WeatherService.Sources;

/// <summary>
/// Fetches raw latest-observation JSON from the National Weather Service API.
/// </summary>
public class WeatherGovFetcher : WeatherFetcherBase
{
    public WeatherGovFetcher(
        IHttpClientFactory httpClientFactory,
        IOptions<WorkerOptions> options,
        ResiliencePipelineProvider<string> pipelineProvider,
        ProviderRateLimiters rateLimiters,
        ILogger<WeatherGovFetcher> logger)
        : base(ResiliencePipelines.WeatherGov, httpClientFactory, options, pipelineProvider, rateLimiters, logger)
    {
    }

    protected override string ProviderName => "Weather.gov";

    /// <summary>Fetches the latest observation for a Weather.gov station id.</summary>
    public virtual Task<string> FetchLatestObservationAsync(string stationId, CancellationToken ct = default)
    {
        string normalized = RequireStationId(stationId);
        var target = new FetchTarget(
            BuildUrl(normalized),
            $"Weather.gov observation not published for station {normalized}",
            $" for station {normalized}");

        return ExecuteFetchAsync(target, ct);
    }

    /// <summary>
    /// Resolves a coordinate to the nearest NWS observation station, or <c>null</c> when Weather.gov
    /// reports none.
    ///
    /// <para>This exists because <c>WaterStation.MLI</c> is a water-gauge id (a USGS site number),
    /// never an NWS call sign — fetching observations by <c>mli</c> 404s for every US station. The
    /// answer is cached in the database, so this runs once per station, not once per cycle.</para>
    /// </summary>
    public virtual async Task<string?> FindNearestStationAsync(
        double latitude, double longitude, CancellationToken ct = default)
    {
        // Weather.gov rejects more than 4 decimal places with an "AdjustPointPrecision" error, and
        // answers /points with a 301 the handler follows (AllowAutoRedirect).
        string point = string.Format(CultureInfo.InvariantCulture, "{0:F4},{1:F4}", latitude, longitude);
        string url = TrimBaseUrl(Options.WeatherGovBaseUrl) + "/points/" + point + "/stations";
        var target = new FetchTarget(
            url,
            $"Weather.gov has no forecast point for {point}",
            $" for point {point}");

        string json;
        try
        {
            json = await ExecuteFetchAsync(target, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // Outside NWS coverage entirely (e.g. a non-US coordinate).
            return null;
        }

        return FirstStationIdentifier(json);
    }

    /// <summary>Pulls the first <c>stationIdentifier</c> out of the GeoJSON feature collection.</summary>
    internal static string? FirstStationIdentifier(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("features", out JsonElement features)
                || features.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement feature in features.EnumerateArray())
            {
                if (feature.TryGetProperty("properties", out JsonElement properties)
                    && properties.TryGetProperty("stationIdentifier", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String)
                {
                    string? value = id.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // The shape guard already proved it is a JSON object; anything else means an unexpected
            // payload, which is indistinguishable here from "no station".
        }

        return null;
    }

    protected override void ConfigureRequest(HttpRequestMessage request)
    {
        // Weather.gov rejects anonymous clients: the User-Agent must identify a reachable contact.
        request.Headers.TryAddWithoutValidation("User-Agent", Options.WeatherGovUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/geo+json");
    }

    private static string RequireStationId(string stationId)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        {
            throw new ArgumentException("stationId must not be null or blank", nameof(stationId));
        }
        return stationId.Trim().ToUpperInvariant();
    }

    private string BuildUrl(string stationId) =>
        TrimBaseUrl(Options.WeatherGovBaseUrl) + "/stations/" + Uri.EscapeDataString(stationId) + "/observations/latest";
}
