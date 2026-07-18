using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;
using WaterService.Configuration;

namespace WaterService.Sources;

/// <summary>
/// Downloads hourly hydrometric CSV files from Environment Canada.
///
/// <para>Transient failures (network errors, 5xx) are retried; sustained failures open the <c>caFeed</c>
/// circuit breaker. A 404 means the feed is simply not published for that station and is surfaced as a
/// <see cref="FileNotFoundException"/> (neither retried nor counted against the breaker).</para>
/// </summary>
public sealed class CsvFetcherCA
{
    private const string ClientName = "waterSource";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger<CsvFetcherCA> _log;

    public CsvFetcherCA(
        IHttpClientFactory httpClientFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<CsvFetcherCA> log)
    {
        _httpClientFactory = httpClientFactory;
        _pipeline = pipelineProvider.GetPipeline(ResiliencePipelines.CaFeed);
        _log = log;
    }

    /// <summary>
    /// Fetches the raw CSV body for a station.
    /// </summary>
    /// <exception cref="FileNotFoundException">The source feed is not published (HTTP 404).</exception>
    public async Task<string> FetchAsync(string state, string mli, CancellationToken ct = default)
    {
        // state/mli are URL-encoded so a hostile or corrupt DB row cannot rewrite the request path.
        string url =
            $"https://dd.weather.gc.ca/today/hydrometric/csv/{Uri.EscapeDataString(state)}/hourly/" +
            $"{Uri.EscapeDataString(state)}_{Uri.EscapeDataString(mli)}_hourly_hydrometric.csv";

        _log.LogDebug("Fetching hydrometric CSV. station={Mli} state={State}", mli, state);

        return await _pipeline.ExecuteAsync(async token =>
        {
            HttpClient client = _httpClientFactory.CreateClient(ClientName);
            using HttpResponseMessage response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new FileNotFoundException(
                    $"HTTP 404: hydrometric CSV not published for CA station {mli} (state {state})");
            }

            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            _log.LogDebug("Fetched station CSV. station={Mli} state={State}", mli, state);
            return body;
        }, ct).ConfigureAwait(false);
    }
}
