using System.Net;
using System.Text.Json;
using Polly.CircuitBreaker;

namespace NewsSearch.Sources;

/// <summary>
/// Semantic search: asks news_idx (on CTRL), which embeds the query with bge-small-en-v1.5 and searches
/// Qdrant. news_search holds no model; news_idx is the only process that does. The HTTP client carries
/// the timeout, retry and circuit breaker (see ServiceRegistration).
/// </summary>
public sealed class VectorSource : ISearchSource
{
    public const string HttpClientName = "newsidx";

    private readonly IHttpClientFactory _clients;
    private readonly ILogger<VectorSource> _logger;

    public VectorSource(IHttpClientFactory clients, ILogger<VectorSource> logger)
    {
        _clients = clients;
        _logger = logger;
    }

    public string Name => "vector";

    public string Description => "Semantic (meaning-based) search: bge-small-en-v1.5 embeddings in Qdrant, via news_idx.";

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        string url = $"api/v1/newsidx/search?q={Uri.EscapeDataString(request.Query)}&limit={request.Limit}"
                     + (request.Country is null ? string.Empty : $"&country={request.Country}");

        HttpResponseMessage response;
        try
        {
            response = await _clients.CreateClient(HttpClientName).GetAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BrokenCircuitException
                                       or TimeoutException or Polly.Timeout.TimeoutRejectedException
                                   && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "news_idx call failed");
            throw new SourceUnavailableException(Name, ex.GetType().Name, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                // news_idx rejected the input; validation here should have caught it first.
                throw new InvalidRequestException(await ErrorMessage(response, cancellationToken).ConfigureAwait(false));
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new SourceUnavailableException(Name, $"news_idx answered {(int)response.StatusCode}");
            }

            using JsonDocument doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return Parse(doc.RootElement);
        }
    }

    /// <summary>news_idx's <c>{data:{items:[…]}}</c> → hits.</summary>
    internal static IReadOnlyList<SearchHit> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("items", out JsonElement items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var hits = new List<SearchHit>(items.GetArrayLength());
        foreach (JsonElement i in items.EnumerateArray())
        {
            string? id = Str(i, "newsId");
            if (id is null)
            {
                continue;
            }
            hits.Add(new SearchHit(id, Str(i, "title") ?? string.Empty, Str(i, "source"), Str(i, "country"),
                Str(i, "date"), i.TryGetProperty("score", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0,
                Str(i, "snippet"), Str(i, "lakeId")));
        }
        return hits;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task<string> ErrorMessage(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "invalid request";
        }
        catch (Exception)
        {
            return "invalid request";
        }
    }
}
