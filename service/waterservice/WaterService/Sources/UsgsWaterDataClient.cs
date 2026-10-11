using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using WaterService.Configuration;
using WaterService.Domain;

namespace WaterService.Sources;

/// <summary>
/// Reads US readings from the USGS Water Data API (<c>api.waterdata.usgs.gov/ogcapi</c>), which replaces
/// the legacy WaterServices <c>nwis/iv</c> feed (decommissioned 2027-02-22).
///
/// <para>The legacy feed was called once per station — about 9,500 calls per hourly cycle. The new API is
/// rate-limited per key, so a cycle instead pages through the whole <c>latest-continuous</c> collection
/// (the newest value of every US series, ~2-3 requests of up to 50,000 rows) and the processor picks its
/// stations out of that snapshot.</para>
///
/// <para>Each page goes through the <c>usFeed</c> pipeline, so transient failures are retried and
/// sustained ones open its breaker. A failed snapshot fails the whole US pass; the next cycle (and the
/// redundant Java service) covers it.</para>
/// </summary>
public class UsgsWaterDataClient
{
    private const string ClientName = "waterSource";
    private const string ApiKeyHeader = "X-Api-Key";
    private const string SitePrefix = "USGS-";
    private const int PageLimit = 50000;
    private const int MaxPages = 20;
    private const int TimeZoneBatchSize = 200;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResiliencePipeline _pipeline;
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly ILogger<UsgsWaterDataClient> _log;

    // Site time zones never change, so they are looked up once per process and kept.
    private readonly ConcurrentDictionary<string, TimeZoneInfo> _timeZones = new(StringComparer.OrdinalIgnoreCase);

    public UsgsWaterDataClient(
        IHttpClientFactory httpClientFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        IOptions<WorkerOptions> options,
        IConfiguration configuration,
        ILogger<UsgsWaterDataClient> log)
    {
        _httpClientFactory = httpClientFactory;
        _pipeline = pipelineProvider.GetPipeline(ResiliencePipelines.UsFeed);
        _baseUrl = options.Value.UsgsBaseUrl.TrimEnd('/');
        _apiKey = configuration["USGS_API_KEY"];
        _log = log;
    }

    /// <summary>
    /// Fetches the latest value of every current USGS series, keyed by bare site number (the
    /// <c>vwWaterStation</c> MLI, without the <c>USGS-</c> prefix).
    /// </summary>
    /// <param name="mli">Restrict the snapshot to one site (startup verification); <c>null</c> for all.</param>
    public virtual async Task<IReadOnlyDictionary<string, List<UsgsObservation>>> FetchLatestAsync(
        string? mli, CancellationToken ct = default)
    {
        string url = $"{_baseUrl}/latest-continuous/items?f=json&skipGeometry=true&limit={PageLimit}"
            + "&properties=monitoring_location_id,parameter_code,statistic_id,time,value";
        if (!string.IsNullOrWhiteSpace(mli))
        {
            url += "&monitoring_location_id=" + Uri.EscapeDataString(SitePrefix + mli.Trim());
        }

        var snapshot = new Dictionary<string, List<UsgsObservation>>(StringComparer.OrdinalIgnoreCase);
        int pages = 0;
        int rows = 0;
        for (string? next = url; next is not null; pages++)
        {
            if (pages == MaxPages)
            {
                throw new InvalidOperationException(
                    $"USGS latest-continuous snapshot did not end after {MaxPages} pages; refusing to continue.");
            }

            FeatureCollection page = await GetPageAsync(next, ct).ConfigureAwait(false);
            if (page.Features is not { Count: > 0 })
            {
                break;
            }

            foreach (Feature feature in page.Features)
            {
                if (ToObservation(feature.Properties) is ({ } site, { } observation))
                {
                    if (!snapshot.TryGetValue(site, out List<UsgsObservation>? list))
                    {
                        snapshot[site] = list = [];
                    }
                    list.Add(observation);
                    rows++;
                }
            }

            next = page.Links?.FirstOrDefault(l => l.Rel == "next")?.Href;
        }

        _log.LogInformation("Fetched USGS latest-continuous snapshot. pages={Pages} series={Rows} sites={Sites}",
            pages, rows, snapshot.Count);
        return snapshot;
    }

    /// <summary>
    /// Makes sure the local time zone of each site is known, looking up only the ones not seen before.
    /// Readings are bucketed by the site's local date, as the legacy WaterML timestamps were.
    /// </summary>
    public virtual async Task EnsureTimeZonesAsync(IEnumerable<string> mlis, CancellationToken ct = default)
    {
        List<string> missing = mlis.Where(m => !_timeZones.ContainsKey(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string[] batch in missing.Chunk(TimeZoneBatchSize))
        {
            string ids = string.Join(",", batch.Select(m => Uri.EscapeDataString(SitePrefix + m)));
            string url = $"{_baseUrl}/monitoring-locations/items?f=json&skipGeometry=true&limit={TimeZoneBatchSize}"
                + "&properties=time_zone_abbreviation,uses_daylight_savings&id=" + ids;

            FeatureCollection page = await GetPageAsync(url, ct).ConfigureAwait(false);
            foreach (Feature feature in page.Features ?? [])
            {
                string? site = StripPrefix(feature.Id);
                if (site is not null)
                {
                    _timeZones[site] = ResolveTimeZone(
                        feature.Properties?.TimeZoneAbbreviation, feature.Properties?.UsesDaylightSavings);
                }
            }

            // A site USGS does not describe falls back to UTC rather than being looked up every cycle.
            foreach (string site in batch)
            {
                if (_timeZones.TryAdd(site, TimeZoneInfo.Utc))
                {
                    _log.LogDebug("No time zone published for USGS site; using UTC. station={Mli}", site);
                }
            }
        }

        if (missing.Count > 0)
        {
            _log.LogInformation("Looked up USGS site time zones. sites={Count}", missing.Count);
        }
    }

    /// <summary>Local time zone of a site; UTC when it was never looked up.</summary>
    public virtual TimeZoneInfo TimeZoneFor(string mli) =>
        _timeZones.TryGetValue(mli, out TimeZoneInfo? zone) ? zone : TimeZoneInfo.Utc;

    /// <summary>
    /// Maps a USGS <c>time_zone_abbreviation</c> (always the standard-time name) plus its daylight-saving
    /// flag to a time zone. DST-observing zones use the IANA zone so the switch dates are right.
    /// </summary>
    internal static TimeZoneInfo ResolveTimeZone(string? abbreviation, string? usesDaylightSavings)
    {
        bool dst = string.Equals(usesDaylightSavings, "Y", StringComparison.OrdinalIgnoreCase);
        (string? iana, double offsetHours) = abbreviation?.Trim().ToUpperInvariant() switch
        {
            "EST" => ("America/New_York", -5),
            "CST" => ("America/Chicago", -6),
            "MST" => ("America/Denver", -7),
            "PST" => ("America/Los_Angeles", -8),
            "AKST" => ("America/Anchorage", -9),
            "HST" => ((string?)null, -10),
            "AST" => ("America/Halifax", -4),
            "SST" => (null, -11),
            "CHST" or "GST" => (null, 10),
            _ => (null, 0),
        };

        if (dst && iana is not null && TimeZoneInfo.TryFindSystemTimeZoneById(iana, out TimeZoneInfo? zone))
        {
            return zone;
        }

        return offsetHours == 0
            ? TimeZoneInfo.Utc
            : TimeZoneInfo.CreateCustomTimeZone(
                abbreviation!, TimeSpan.FromHours(offsetHours), abbreviation, abbreviation);
    }

    private async Task<FeatureCollection> GetPageAsync(string url, CancellationToken ct) =>
        await _pipeline.ExecuteAsync(async token =>
        {
            HttpClient client = _httpClientFactory.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                request.Headers.TryAddWithoutValidation(ApiKeyHeader, _apiKey);
            }

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Not transient on a retry timescale: the hourly quota is spent. Say so plainly.
                throw new HttpRequestException(
                    "HTTP 429: USGS Water Data API rate limit reached (check USGS_API_KEY).",
                    null, response.StatusCode);
            }

            response.EnsureSuccessStatusCode();

            try
            {
                await using Stream body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<FeatureCollection>(body, JsonOptions, token)
                           .ConfigureAwait(false)
                       ?? new FeatureCollection();
            }
            catch (Exception ex) when (ex is IOException || ex.InnerException is IOException)
            {
                // A body cut off mid-read surfaces here, after clean headers. Rethrown as an
                // HttpRequestException so the pipeline retries it like any other transport failure.
                throw new HttpRequestException("I/O error while reading USGS Water Data API response", ex);
            }
        }, ct).ConfigureAwait(false);

    private static (string? Site, UsgsObservation? Observation) ToObservation(FeatureProperties? p)
    {
        string? site = StripPrefix(p?.MonitoringLocationId);
        string? value = p?.Value.ValueKind switch
        {
            JsonValueKind.String => p.Value.GetString(),
            JsonValueKind.Number => p.Value.GetRawText(),
            _ => null,
        };
        if (site is null || string.IsNullOrWhiteSpace(p!.ParameterCode) || string.IsNullOrWhiteSpace(value)
            || !DateTimeOffset.TryParse(p.Time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                out DateTimeOffset time))
        {
            return (null, null);
        }

        return (site, new UsgsObservation(p.ParameterCode, p.StatisticId, time, value.Trim()));
    }

    private static string? StripPrefix(string? id) =>
        id is not null && id.StartsWith(SitePrefix, StringComparison.OrdinalIgnoreCase) && id.Length > SitePrefix.Length
            ? id.Substring(SitePrefix.Length)
            : null;

    // --- GeoJSON shapes (only the fields requested) ---------------------------------------------------

    private sealed class FeatureCollection
    {
        public List<Feature>? Features { get; set; }

        public List<Link>? Links { get; set; }
    }

    private sealed class Feature
    {
        public string? Id { get; set; }

        public FeatureProperties? Properties { get; set; }
    }

    private sealed class Link
    {
        public string? Rel { get; set; }

        public string? Href { get; set; }
    }

    private sealed class FeatureProperties
    {
        [JsonPropertyName("monitoring_location_id")]
        public string? MonitoringLocationId { get; set; }

        [JsonPropertyName("parameter_code")]
        public string? ParameterCode { get; set; }

        [JsonPropertyName("statistic_id")]
        public string? StatisticId { get; set; }

        [JsonPropertyName("time")]
        public string? Time { get; set; }

        // Published as a string today; a JsonElement also accepts a bare number should that change.
        [JsonPropertyName("value")]
        public JsonElement Value { get; set; }

        [JsonPropertyName("time_zone_abbreviation")]
        public string? TimeZoneAbbreviation { get; set; }

        [JsonPropertyName("uses_daylight_savings")]
        public string? UsesDaylightSavings { get; set; }
    }
}
