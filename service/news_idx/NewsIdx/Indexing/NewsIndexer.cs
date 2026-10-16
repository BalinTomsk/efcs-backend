using Microsoft.Extensions.Options;
using NewsIdx.Configuration;
using NewsIdx.Data;
using NewsIdx.Domain;
using NewsIdx.Embedding;
using NewsIdx.Search;
using Polly;
using Polly.Registry;
using Prometheus;

namespace NewsIdx.Indexing;

/// <summary>
/// Brings one article's entry in the index in line with the database. The single code path behind an event,
/// the sync and a manual reindex — so all three behave the same way.
///
/// <para>The database decides, never the caller: a published article is (re)indexed, anything else (draft,
/// deleted, unknown id) is removed from the index. That makes every call idempotent and safe to repeat.</para>
/// </summary>
public class NewsIndexer
{
    private static readonly Counter Outcomes = Metrics.CreateCounter(
        "newsidx_index_articles_total", "Articles processed by the indexer, by outcome.", "outcome");
    private static readonly Counter ChunksIndexed = Metrics.CreateCounter(
        "newsidx_index_chunks_total", "Chunks embedded and stored.");

    private readonly INewsArticleSource _source;
    private readonly ITextEmbedder _embedder;
    private readonly IVectorStore _store;
    private readonly IKeywordStore _keywords;
    private readonly Bm25Encoder _bm25;
    private readonly ResiliencePipeline _mysql;
    private readonly ChunkingOptions _chunking;
    private readonly ILogger<NewsIndexer> _logger;

    public NewsIndexer(
        INewsArticleSource source,
        ITextEmbedder embedder,
        IVectorStore store,
        IKeywordStore keywords,
        ResiliencePipelineProvider<string> pipelines,
        IOptions<NewsIdxOptions> options,
        ILogger<NewsIndexer> logger)
    {
        _source = source;
        _embedder = embedder;
        _store = store;
        _keywords = keywords;
        _bm25 = new Bm25Encoder(options.Value.Bm25);
        _mysql = pipelines.GetPipeline(ResiliencePipelines.MySql);
        _chunking = options.Value.Chunking;
        _logger = logger;
    }

    /// <summary>Indexes (vectors + BM25) or removes one article. Throws when MySQL, the model or Qdrant fails.</summary>
    public virtual async Task<IndexOutcome> IndexAsync(string newsId, CancellationToken cancellationToken)
    {
        string id = NewsIds.Normalize(newsId);
        NewsArticle? article = await _mysql
            .ExecuteAsync(async ct => await _source.GetPublishedAsync(id, ct).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

        if (article is null)
        {
            await _store.DeleteArticleAsync(id, cancellationToken).ConfigureAwait(false);
            await _keywords.DeleteArticleAsync(id, cancellationToken).ConfigureAwait(false);
            Outcomes.WithLabels("removed").Inc();
            _logger.LogInformation("News article removed from the index (not published). newsId={NewsId}", id);
            return new IndexOutcome(id, Indexed: false, Chunks: 0);
        }

        IReadOnlyList<string> passages = NewsChunker.Chunk(article, _chunking);
        IReadOnlyList<float[]> vectors = await _embedder.EmbedPassagesAsync(passages, cancellationToken).ConfigureAwait(false);
        var chunks = passages.Select((text, i) => new IndexedChunk(i, text, vectors[i])).ToList();

        await _store.ReplaceArticleAsync(article, chunks, _embedder.ModelName, cancellationToken).ConfigureAwait(false);

        (string title, string body) = Bm25Encoder.Text(article);
        await _keywords.ReplaceArticleAsync(article, body, _bm25.EncodeDocument(title, body), cancellationToken).ConfigureAwait(false);
        Outcomes.WithLabels("indexed").Inc();
        ChunksIndexed.Inc(chunks.Count);
        _logger.LogInformation("News article indexed. newsId={NewsId} chunks={Chunks}", id, chunks.Count);
        return new IndexOutcome(id, Indexed: true, Chunks: chunks.Count);
    }

    /// <summary>Removes an article without asking the database (used for ids the database no longer has).</summary>
    public virtual async Task RemoveAsync(string newsId, CancellationToken cancellationToken)
    {
        await _store.DeleteArticleAsync(newsId, cancellationToken).ConfigureAwait(false);
        await _keywords.DeleteArticleAsync(newsId, cancellationToken).ConfigureAwait(false);
        Outcomes.WithLabels("removed").Inc();
    }
}

public sealed record IndexOutcome(string NewsId, bool Indexed, int Chunks);
