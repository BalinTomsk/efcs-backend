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

    /// <summary>
    /// Fetches the GRIDPOINT FORECAST for a coordinate — the actual multi-day forecast, not the latest
    /// observation.
    ///
    /// <para>Two calls, because the forecast URL is not derivable from a coordinate:
    /// <c>/points/{lat},{lon}</c> answers with the gauge's grid cell and, in
    /// <c>properties.forecast</c>, the URL of that cell's forecast. The same two API quirks as
    /// <see cref="FindNearestStationAsync"/> apply — coordinates rounded to 4 decimal places, and a 301
    /// on <c>/points</c> that the handler follows.</para>
    ///
    /// <para>Requested with <c>units=si</c>, so the periods come back in °C and km/h and the converter
    /// has no unit conversion to do at all.</para>
    ///
    /// <para>Returns <c>null</c> when the coordinate is outside NWS coverage, which the caller turns
    /// into a skip rather than a failure — the same treatment an unpublished feed gets.</para>
    /// </summary>
    public virtual async Task<string?> FetchGridpointForecastAsync(
        double latitude, double longitude, CancellationToken ct = default)
    {
        string point = string.Format(CultureInfo.InvariantCulture, "{0:F4},{1:F4}", latitude, longitude);
        string pointUrl = TrimBaseUrl(Options.WeatherGovBaseUrl) + "/points/" + point;

        string pointJson;
        try
        {
            pointJson = await ExecuteFetchAsync(
                new FetchTarget(pointUrl, $"Weather.gov has no forecast point for {point}", $" for point {point}"),
                ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // Outside NWS coverage entirely (e.g. a non-US coordinate). A permanent answer.
            return null;
        }

        string? forecastUrl = ForecastUrlOf(pointJson);
        if (string.IsNullOrWhiteSpace(forecastUrl))
        {
            return null;
        }

        string url = forecastUrl + (forecastUrl.Contains('?') ? "&" : "?") + "units=si";
        try
        {
            return await ExecuteFetchAsync(
                new FetchTarget(url, $"Weather.gov publishes no forecast for {point}", $" for forecast {point}"),
                ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Reads <c>properties.forecast</c> out of a <c>/points</c> response.</summary>
    internal static string? ForecastUrlOf(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("properties", out JsonElement properties)
                && properties.TryGetProperty("forecast", out JsonElement forecast)
                && forecast.ValueKind == JsonValueKind.String)
            {
                return forecast.GetString();
            }
        }
        catch (JsonException)
        {
            // Not the document we expected; the caller treats a missing URL as "no coverage".
        }
        return null;
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
