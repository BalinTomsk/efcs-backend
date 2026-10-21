using System.Net;
using System.Text.Json;
using Polly.CircuitBreaker;

namespace NewsSearch.Sources;

/// <summary>
/// A source answered by news_idx (on CTRL), which holds both indexes in Qdrant. news_search holds no model
/// and no index. Every news_idx source shares one HTTP client, so they share its timeout, retry and circuit
/// breaker (see ServiceRegistration): when news_idx is down, all of them answer 503 at once.
/// </summary>
public abstract class NewsIdxSource : ISearchSource
{
    public const string HttpClientName = "newsidx";

    private readonly IHttpClientFactory _clients;
    private readonly ILogger _logger;

    protected NewsIdxSource(IHttpClientFactory clients, ILogger logger)
    {
        _clients = clients;
        _logger = logger;
    }

    public abstract string Name { get; }

    public abstract string Description { get; }

    /// <summary>The news_idx route under <c>/api/v1/newsidx/</c>.</summary>
    protected abstract string Endpoint { get; }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        string url = $"api/v1/newsidx/{Endpoint}?q={Uri.EscapeDataString(request.Query)}&limit={request.Limit}"
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
            _logger.LogWarning(ex, "news_idx call failed. source={Source}", Name);
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
                Str(i, "snippet"), Str(i, "lakeId"),
                i.TryGetProperty("relevance", out JsonElement r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : null,
                i.TryGetProperty("missing", out JsonElement m) && m.ValueKind == JsonValueKind.Array
                    ? m.EnumerateArray().Where(w => w.ValueKind == JsonValueKind.String).Select(w => w.GetString()!).ToList()
                    : null));
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

/// <summary>Semantic search: news_idx embeds the query with bge-small-en-v1.5 and searches Qdrant's vectors.</summary>
public sealed class VectorSource(IHttpClientFactory clients, ILogger<VectorSource> logger) : NewsIdxSource(clients, logger)
{
    public override string Name => "vector";

    public override string Description => "Semantic (meaning-based) search: bge-small-en-v1.5 embeddings in Qdrant, via news_idx.";

    protected override string Endpoint => "search";
}

/// <summary>
/// Keyword search ranked by BM25: news_idx matches the query's stemmed words in a Qdrant sparse index, rarer
/// words weighing more. Finds exact names and terms (a lake, a species, "keta") that meaning-based search
/// can miss.
/// </summary>
public sealed class Bm25Source(IHttpClientFactory clients, ILogger<Bm25Source> logger) : NewsIdxSource(clients, logger)
{
    public override string Name => "bm25";

    public override string Description => "Keyword search ranked by BM25 (stemmed words, rare words weigh more), via news_idx.";

    protected override string Endpoint => "bm25";
}
