using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewsIdx.Configuration;
using NewsIdx.Data;
using NewsIdx.Domain;
using NewsIdx.Embedding;
using NewsIdx.Indexing;
using NewsIdx.Messaging;
using NewsIdx.Search;
using Polly.Registry;

namespace NewsIdx.Tests;

/// <summary>Scriptable article source: published articles by id, or a failure.</summary>
internal sealed class FakeArticleSource : INewsArticleSource
{
    public ConcurrentDictionary<string, NewsArticle> Published { get; } = new();
    public Exception? Failure { get; set; }
    public int Reads;

    public Task<NewsArticle?> GetPublishedAsync(string newsId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Reads);
        if (Failure is not null) throw Failure;
        return Task.FromResult(Published.TryGetValue(newsId, out NewsArticle? a) ? a : null);
    }

    public Task<IReadOnlyList<NewsVersion>> ListPublishedVersionsAsync(CancellationToken cancellationToken = default)
    {
        if (Failure is not null) throw Failure;
        return Task.FromResult<IReadOnlyList<NewsVersion>>(
            Published.Values.Select(a => new NewsVersion(a.NewsId, a.LastEditUtc)).ToList());
    }

    public NewsArticle Add(string title, string body, DateTime? edited = null, string country = "CA")
    {
        var article = Article(Guid.NewGuid().ToString("D"), title, body, edited, country);
        Published[article.NewsId] = article;
        return article;
    }

    public static NewsArticle Article(string id, string title, string body, DateTime? edited = null, string country = "CA") =>
        new(id, title, "Author", "Source", [body], [], country,
            new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), null, [],
            edited ?? new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc));
}

/// <summary>
/// Bag-of-words embedder: each word hashes to a dimension, so texts sharing words are close. Enough to check
/// ranking and grouping without the ONNX model.
/// </summary>
internal sealed class FakeEmbedder : ITextEmbedder
{
    public int Dimension => 64;
    public string ModelName => "fake";
    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken)
    {
        if (Failure is not null) throw Failure;
        return Task.FromResult<IReadOnlyList<float[]>>(passages.Select(Embed).ToList());
    }

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken) => Task.FromResult(Embed(query));

    private float[] Embed(string text)
    {
        var v = new float[Dimension];
        foreach (string w in text.ToLowerInvariant().Split(' ', '\n', '.', ','))
        {
            if (w.Length > 2) v[(int)((uint)w.GetHashCode(StringComparison.Ordinal) % Dimension)] += 1;
        }
        return BgeSmallEmbedder.Normalize(v);
    }
}

/// <summary>In-memory vector store with exact cosine search and the same replace/trim semantics as Qdrant's.</summary>
internal sealed class FakeVectorStore : IVectorStore
{
    public ConcurrentDictionary<Guid, (NewsArticle Article, IndexedChunk Chunk)> Points { get; } = new();
    public int? CollectionDimension;

    public Task EnsureCollectionAsync(int dimension, CancellationToken cancellationToken)
    {
        CollectionDimension = dimension;
        return Task.CompletedTask;
    }

    public Task<ulong> CountAsync(CancellationToken cancellationToken) => Task.FromResult((ulong)Points.Count);

    public Task ReplaceArticleAsync(NewsArticle article, IReadOnlyList<IndexedChunk> chunks, string model, CancellationToken cancellationToken)
    {
        foreach (IndexedChunk c in chunks)
        {
            Points[NewsIds.PointId(article.NewsId, c.Chunk)] = (article, c);
        }
        foreach (var stale in Points.Where(p => p.Value.Article.NewsId == article.NewsId && p.Value.Chunk.Chunk >= chunks.Count).ToList())
        {
            Points.TryRemove(stale.Key, out _);
        }
        return Task.CompletedTask;
    }

    public Task DeleteArticleAsync(string newsId, CancellationToken cancellationToken)
    {
        foreach (var p in Points.Where(p => p.Value.Article.NewsId == newsId).ToList())
        {
            Points.TryRemove(p.Key, out _);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, DateTime>> ListIndexedVersionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, DateTime>>(Points.Values
            .Where(p => p.Chunk.Chunk == 0)
            .ToDictionary(p => p.Article.NewsId, p => Seconds(p.Article.LastEditUtc)));

    public Task<IReadOnlyList<ChunkHit>> SearchAsync(float[] vector, int limit, string? country, float minScore, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ChunkHit>>(Points.Values
            .Where(p => country is null || p.Article.Country == country)
            .Select(p => new ChunkHit(p.Article.NewsId, p.Chunk.Chunk, Dot(vector, p.Chunk.Vector), p.Article.Title,
                p.Article.Source, p.Article.Country, p.Article.StampUtc, p.Article.LakeId, p.Chunk.Text))
            .Where(h => h.Score >= minScore)
            .OrderByDescending(h => h.Score)
            .Take(limit)
            .ToList());

    public int ChunksOf(string newsId) => Points.Values.Count(p => p.Article.NewsId == newsId);

    private static float Dot(float[] a, float[] b) => a.Zip(b, (x, y) => x * y).Sum();

    // Qdrant stores last_edit_unix in whole seconds.
    private static DateTime Seconds(DateTime t) => DateTimeOffset.FromUnixTimeSeconds(new DateTimeOffset(t).ToUnixTimeSeconds()).UtcDateTime;
}

/// <summary>
/// In-memory BM25 store: computes Qdrant's IDF (<c>ln(1 + (N − n + 0.5)/(n + 0.5))</c>) over the stored
/// documents at query time, exactly like the <c>modifier = idf</c> collection.
/// </summary>
internal sealed class FakeKeywordStore : IKeywordStore
{
    public ConcurrentDictionary<string, (NewsArticle Article, string Text, SparseTerms Terms)> Docs { get; } = new();
    public bool Ensured;

    public Task EnsureCollectionAsync(CancellationToken cancellationToken)
    {
        Ensured = true;
        return Task.CompletedTask;
    }

    public Task<ulong> CountAsync(CancellationToken cancellationToken) => Task.FromResult((ulong)Docs.Count);

    public Task ReplaceArticleAsync(NewsArticle article, string text, SparseTerms terms, CancellationToken cancellationToken)
    {
        Docs[NewsIds.Normalize(article.NewsId)] = (article, text, terms);
        return Task.CompletedTask;
    }

    public Task DeleteArticleAsync(string newsId, CancellationToken cancellationToken)
    {
        Docs.TryRemove(NewsIds.Normalize(newsId), out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, DateTime>> ListIndexedVersionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, DateTime>>(Docs.ToDictionary(d => d.Key,
            d => DateTimeOffset.FromUnixTimeSeconds(new DateTimeOffset(d.Value.Article.LastEditUtc).ToUnixTimeSeconds()).UtcDateTime));

    public Exception? TermsFailure { get; set; }

    public Task<IReadOnlyDictionary<string, uint[]>> GetTermsAsync(IReadOnlyCollection<string> newsIds, CancellationToken cancellationToken)
    {
        if (TermsFailure is not null) throw TermsFailure;
        return Task.FromResult<IReadOnlyDictionary<string, uint[]>>(newsIds
            .Select(NewsIds.Normalize)
            .Where(Docs.ContainsKey)
            .ToDictionary(id => id, id => Docs[id].Terms.Indices.Order().ToArray()));
    }

    public Task<IReadOnlyList<KeywordHit>> SearchAsync(SparseTerms query, int limit, string? country, CancellationToken cancellationToken)
    {
        int n = Docs.Count;
        var hits = new List<KeywordHit>();
        foreach (var d in Docs.Values.Where(d => country is null || d.Article.Country == country))
        {
            float score = 0;
            foreach (uint term in query.Indices)
            {
                int at = Array.IndexOf(d.Terms.Indices, term);
                if (at < 0) continue;
                int df = Docs.Values.Count(o => o.Terms.Indices.Contains(term));
                score += (float)Math.Log(1 + (n - df + 0.5) / (df + 0.5)) * d.Terms.Values[at];
            }
            if (score > 0)
            {
                hits.Add(new KeywordHit(d.Article.NewsId, score, d.Article.Title, d.Article.Source, d.Article.Country,
                    d.Article.StampUtc, d.Article.LakeId, d.Text));
            }
        }
        return Task.FromResult<IReadOnlyList<KeywordHit>>(hits.OrderByDescending(h => h.Score).Take(limit).ToList());
    }
}

/// <summary>Queue fake: messages to hand out, plus everything published back.</summary>
internal sealed class FakeQueue : IMessageQueue
{
    public Queue<string> Pending { get; } = new();
    public List<string> Published { get; } = [];

    public Task DeclareAsync(string queue, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> GetAsync(string queue, int count, CancellationToken cancellationToken)
    {
        var batch = new List<string>();
        while (batch.Count < count && Pending.TryDequeue(out string? m)) batch.Add(m);
        return Task.FromResult<IReadOnlyList<string>>(batch);
    }

    public Task<bool> PublishAsync(string queue, string body, CancellationToken cancellationToken)
    {
        Published.Add(body);
        return Task.FromResult(true);
    }
}

internal static class TestSupport
{
    public static NewsIdxOptions Options(Action<NewsIdxOptions>? configure = null)
    {
        var options = new NewsIdxOptions();
        configure?.Invoke(options);
        return options;
    }

    public static ResiliencePipelineProvider<string> Pipelines() =>
        new ServiceCollection().AddLogging().AddNewsIdxResiliencePipelines().BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>();

    public static NewsIndexer Indexer(
        FakeArticleSource source, FakeVectorStore store, FakeEmbedder? embedder = null, NewsIdxOptions? options = null,
        FakeKeywordStore? keywords = null) =>
        new(source, embedder ?? new FakeEmbedder(), store, keywords ?? new FakeKeywordStore(), Pipelines(),
            Microsoft.Extensions.Options.Options.Create(options ?? Options()), NullLogger<NewsIndexer>.Instance);

    public static NewsSyncService Sync(
        FakeArticleSource source, FakeVectorStore store, NewsIndexer? indexer = null, FakeKeywordStore? keywords = null)
    {
        keywords ??= new FakeKeywordStore();
        return new(source, store, keywords, indexer ?? Indexer(source, store, keywords: keywords), Pipelines(),
            TimeProvider.System, NullLogger<NewsSyncService>.Instance);
    }

    public static string Event(string newsId, string action = "updated", int? attempt = null)
    {
        string a = attempt is null ? string.Empty : $",\"attempt\":{attempt}";
        return $"{{\"schema\":\"fishfind.news-event.v1\",\"eventId\":\"{Guid.NewGuid()}\",\"eventType\":\"fishfind.news.article\","
               + $"\"action\":\"{action}\",\"aggregateId\":\"{newsId}\",\"occurredUtc\":\"2026-10-14T00:00:00Z\",\"source\":\"fishfind-frontend\"{a}}}";
    }

    public static string Words(int n, string word = "walleye") => string.Join(' ', Enumerable.Range(0, n).Select(i => $"{word}{i % 7}"));
}
