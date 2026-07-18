using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;
using WaterService.Configuration;

namespace WaterService.Sources;

/// <summary>
/// Downloads WaterML payloads from USGS for one station.
///
/// <para>Transient failures (network errors such as premature EOF / socket timeouts, and 5xx) are
/// retried; sustained failures open the <c>usFeed</c> circuit breaker. A 404 means the feed is not
/// published for that station and is surfaced as a <see cref="FileNotFoundException"/>.</para>
/// </summary>
public sealed class XmlFetcherUS
{
    private const string ClientName = "waterSource";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger<XmlFetcherUS> _log;

    public XmlFetcherUS(
        IHttpClientFactory httpClientFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<XmlFetcherUS> log)
    {
        _httpClientFactory = httpClientFactory;
        _pipeline = pipelineProvider.GetPipeline(ResiliencePipelines.UsFeed);
        _log = log;
    }

    /// <summary>
    /// Fetches the USGS WaterML document for a station.
    /// </summary>
    /// <exception cref="FileNotFoundException">The source feed is not published (HTTP 404).</exception>
    public async Task<string> FetchAsync(string state, string mli, CancellationToken ct = default)
    {
        // mli is URL-encoded so a DB value cannot append or override query parameters.
        string url =
            $"https://waterservices.usgs.gov/nwis/iv/?sites={Uri.EscapeDataString(mli)}&period=P3D&format=waterml";

        _log.LogDebug("Fetching USGS WaterML. station={Mli} state={State}", mli, state);

        return await _pipeline.ExecuteAsync(async token =>
        {
            HttpClient client = _httpClientFactory.CreateClient(ClientName);
            using HttpResponseMessage response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new FileNotFoundException(
                    $"HTTP 404: WaterML not published for US station {mli} (state {state})");
            }

            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            _log.LogDebug("Fetched USGS WaterML. station={Mli} state={State}", mli, state);
            return body;
        }, ct).ConfigureAwait(false);
    }
}
