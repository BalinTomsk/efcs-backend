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
    private readonly ILogger<NewsSearchService>? _logger;

    public NewsSearchService(
        ITextEmbedder embedder, IVectorStore store, IKeywordStore keywords, IOptions<NewsIdxOptions> options,
        ILogger<NewsSearchService>? logger = null)
    {
        _embedder = embedder;
        _store = store;
        _keywords = keywords;
        _options = options.Value.Search;
        _logger = logger;
    }

    public async Task<NewsSearchResult> SearchAsync(string? query, int? limit, string? country, CancellationToken cancellationToken)
    {
        (string q, int take, string? c) = Validate(query, limit, country);

        using (Latency.NewTimer())
        {
            float[] vector = await _embedder.EmbedQueryAsync(q, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ChunkHit> chunks = await _store
                .SearchAsync(vector, take * OverFetch, c, _options.MinScore, cancellationToken).ConfigureAwait(false);

            List<ChunkHit> best = chunks
                .GroupBy(h => h.NewsId, StringComparer.Ordinal)
                .Select(g => g.MaxBy(h => h.Score)!)
                .OrderByDescending(h => h.Score)
                .Take(take)
                .ToList();
            IReadOnlyDictionary<string, IReadOnlyList<string>> missing =
                await MissingWordsAsync(best.Select(h => h.NewsId).ToList(), q, cancellationToken).ConfigureAwait(false);

            List<NewsHit> hits = best
                .Select(h => new NewsHit(
                    h.NewsId, h.Title, h.Source, h.Country, h.StampUtc.ToString("yyyy-MM-dd"),
                    MathF.Round(h.Score, 4), Snippet(h.Text, h.Title), h.LakeId,
                    VectorRelevance(h.Score), missing.GetValueOrDefault(h.NewsId)))
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

            IReadOnlyDictionary<string, IReadOnlyList<string>> missing =
                await MissingWordsAsync(found.Select(h => h.NewsId).ToList(), q, cancellationToken).ConfigureAwait(false);
            float top = found.Count == 0 ? 0 : found.Max(h => h.Score);

            List<NewsHit> hits = found
                .OrderByDescending(h => h.Score)
                .Select(h => new NewsHit(
                    h.NewsId, h.Title, h.Source, h.Country, h.StampUtc.ToString("yyyy-MM-dd"),
                    MathF.Round(h.Score, 4), MatchSnippet(h.Text, q), h.LakeId,
                    Bm25Relevance(h.Score, top), missing.GetValueOrDefault(h.NewsId)))
                .ToList();

            return new NewsSearchResult(q, c, hits.Count, hits);
        }
    }

    /// <summary>Vector relevance: the cosine similarity as a percent (0.81 → 81), clamped to 0..100.</summary>
    internal static int VectorRelevance(float cosine) => (int)Math.Clamp(MathF.Round(cosine * 100), 0, 100);

    /// <summary>
    /// BM25 relevance: the score as a percent of this query's best BM25 hit (best = 100). BM25 scores have no fixed
    /// ceiling, so this ranks hits within one search; which words a hit lacks is shown by <c>missing</c>.
    /// </summary>
    internal static int Bm25Relevance(float score, float top) =>
        top <= 0 ? 0 : (int)Math.Clamp(MathF.Round(score / top * 100), 0, 100);

    /// <summary>
    /// For each article, the query words it does not contain anywhere (title, paragraphs or captions), like
    /// Google's "Missing: ...". Read from the article's stored BM25 terms, so it covers the whole article and works
    /// for either engine. An article that is not in the BM25 index is left out (unknown, not "nothing missing"), and
    /// a failed lookup leaves every article out: the search itself never fails because of it.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> MissingWordsAsync(
        IReadOnlyCollection<string> newsIds, string query, CancellationToken cancellationToken)
    {
        var missing = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        IReadOnlyList<(string Word, uint Term)> words = Bm25Encoder.QueryWords(query);
        if (words.Count == 0 || newsIds.Count == 0)
        {
            return missing;
        }

        IReadOnlyDictionary<string, uint[]> terms;
        try
        {
            terms = await _keywords.GetTermsAsync(newsIds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Could not read BM25 terms; missing words left out.");
            return missing;
        }

        foreach ((string id, uint[] docTerms) in terms)
        {
            missing[id] = words.Where(w => Array.BinarySearch(docTerms, w.Term) < 0).Select(w => w.Word).ToList();
        }
        return missing;
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
/// higher is better, and the two scales are not comparable. <c>relevance</c> is the same as a percent 0..100
/// (vector: cosine x 100; bm25: share of the query's best BM25 score). <c>missing</c>: query words the article
/// does not contain (empty = all found; null = unknown, the article is not in the BM25 index).
/// </summary>
public sealed record NewsHit(
    string NewsId, string Title, string? Source, string? Country, string Date, float Score, string Snippet, string? LakeId,
    int Relevance, IReadOnlyList<string>? Missing);

/// <summary>Client input error (→ HTTP 400).</summary>
public sealed class InvalidRequestException(string message) : Exception(message);
