using Microsoft.Extensions.Options;
using NewsIdx.Configuration;
using NewsIdx.Embedding;
using Prometheus;

namespace NewsIdx.Search;

/// <summary>
/// News search over the two indexes. <see cref="SearchAsync"/> is semantic: embeds the query, asks Qdrant for the nearest chunks and folds them into one hit
/// per article (its best-scoring chunk). Validates input first, so a bad request is a 400 that never
/// reaches the model or Qdrant.
/// </summary>
public class NewsSearchService
{
    /// <summary>Chunks fetched per article wanted — long articles contribute several near-duplicate chunks.</summary>
    internal const int OverFetch = 4;

    private static readonly Histogram Latency = Metrics.CreateHistogram(
        "newsidx_search_seconds", "Search latency, embedding included.");

    private static readonly Histogram Bm25Latency = Metrics.CreateHistogram(
        "newsidx_bm25_search_seconds", "BM25 keyword search latency.");

    private readonly ITextEmbedder _embedder;
    private readonly IVectorStore _store;
    private readonly IKeywordStore _keywords;
    private readonly SearchOptions _options;

    public NewsSearchService(ITextEmbedder embedder, IVectorStore store, IKeywordStore keywords, IOptions<NewsIdxOptions> options)
    {
        _embedder = embedder;
        _store = store;
        _keywords = keywords;
        _options = options.Value.Search;
    }

    public async Task<NewsSearchResult> SearchAsync(string? query, int? limit, string? country, CancellationToken cancellationToken)
    {
        (string q, int take, string? c) = Validate(query, limit, country);

        using (Latency.NewTimer())
        {
            float[] vector = await _embedder.EmbedQueryAsync(q, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ChunkHit> chunks = await _store
                .SearchAsync(vector, take * OverFetch, c, _options.MinScore, cancellationToken).ConfigureAwait(false);

            List<NewsHit> hits = chunks
                .GroupBy(h => h.NewsId, StringComparer.Ordinal)
                .Select(g => g.MaxBy(h => h.Score)!)
                .OrderByDescending(h => h.Score)
                .Take(take)
                .Select(h => new NewsHit(
                    h.NewsId, h.Title, h.Source, h.Country, h.StampUtc.ToString("yyyy-MM-dd"),
                    MathF.Round(h.Score, 4), Snippet(h.Text, h.Title), h.LakeId))
                .ToList();

            return new NewsSearchResult(q, c, hits.Count, hits);
        }
    }

    /// <summary>
    /// Keyword search ranked by BM25: exact words (stemmed), weighted by how rare they are across all articles.
    /// One point per article, so no grouping. A query made only of stop words ("the") finds nothing.
    /// </summary>
    public async Task<NewsSearchResult> SearchBm25Async(string? query, int? limit, string? country, CancellationToken cancellationToken)
    {
        (string q, int take, string? c) = Validate(query, limit, country);

        using (Bm25Latency.NewTimer())
        {
            IReadOnlyList<KeywordHit> found = await _keywords
                .SearchAsync(Bm25Encoder.EncodeQuery(q), take, c, cancellationToken).ConfigureAwait(false);

            List<NewsHit> hits = found
                .Select(h => new NewsHit(
                    h.NewsId, h.Title, h.Source, h.Country, h.StampUtc.ToString("yyyy-MM-dd"),
                    MathF.Round(h.Score, 4), MatchSnippet(h.Text, q), h.LakeId))
                .ToList();

            return new NewsSearchResult(q, c, hits.Count, hits);
        }
    }

    private (string Query, int Limit, string? Country) Validate(string? query, int? limit, string? country)
    {
        string q = (query ?? string.Empty).Trim();
        if (q.Length == 0)
        {
            throw new InvalidRequestException("q is required");
        }
        if (q.Length > _options.MaxQueryLength)
        {
            throw new InvalidRequestException($"q must be at most {_options.MaxQueryLength} characters");
        }

        int take = limit ?? _options.DefaultLimit;
        if (take < 1 || take > _options.MaxLimit)
        {
            throw new InvalidRequestException($"limit must be between 1 and {_options.MaxLimit}");
        }

        string? c = null;
        if (!string.IsNullOrWhiteSpace(country))
        {
            c = country.Trim().ToUpperInvariant();
            if (c.Length != 2 || !char.IsAsciiLetterUpper(c[0]) || !char.IsAsciiLetterUpper(c[1]))
            {
                throw new InvalidRequestException($"country must be a two-letter code; got '{country.Trim()}'");
            }
        }

        return (q, take, c);
    }

    /// <summary>
    /// About 240 characters of the body around the first word matching the query, starting at a word
    /// boundary; the opening of the body when nothing matches (the match was in the title).
    /// </summary>
    internal static string MatchSnippet(string body, string query)
    {
        int at = Bm25Encoder.FirstMatch(body, query);
        if (at <= 60)
        {
            return Snippet(body, string.Empty);
        }

        int from = body.LastIndexOf(' ', at - 60) + 1;
        string window = body[from..];
        return "…" + (window.Length <= 240 ? window : Snippet(window, string.Empty));
    }

    /// <summary>The passage without its leading title line, cut to about 240 characters at a word boundary.</summary>
    internal static string Snippet(string text, string title)
    {
        string body = text.StartsWith(title + "\n", StringComparison.Ordinal) ? text[(title.Length + 1)..] : text;
        if (body.Length <= 240)
        {
            return body;
        }

        int cut = body.LastIndexOf(' ', 240);
        return body[..(cut > 120 ? cut : 240)] + "…";
    }
}

public sealed record NewsSearchResult(string Query, string? Country, int Count, IReadOnlyList<NewsHit> Items);

/// <summary>
/// One article. <c>score</c> is the cosine similarity of its best chunk (vector) or its BM25 score (bm25);
/// higher is better, and the two scales are not comparable.
/// </summary>
public sealed record NewsHit(
    string NewsId, string Title, string? Source, string? Country, string Date, float Score, string Snippet, string? LakeId);

/// <summary>Client input error (→ HTTP 400).</summary>
public sealed class InvalidRequestException(string message) : Exception(message);
