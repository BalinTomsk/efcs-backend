using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewsFind.Configuration;
using NewsFind.Data;
using NewsFind.Domain;
using NewsFind.Embedding;
using NewsFind.Indexing;
using NewsFind.Messaging;
using NewsFind.Search;
using Polly.Registry;

namespace NewsFind.Tests;

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
    public static NewsFindOptions Options(Action<NewsFindOptions>? configure = null)
    {
        var options = new NewsFindOptions();
        configure?.Invoke(options);
        return options;
    }

    public static ResiliencePipelineProvider<string> Pipelines() =>
        new ServiceCollection().AddLogging().AddNewsFindResiliencePipelines().BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>();

    public static NewsIndexer Indexer(FakeArticleSource source, FakeVectorStore store, FakeEmbedder? embedder = null, NewsFindOptions? options = null) =>
        new(source, embedder ?? new FakeEmbedder(), store, Pipelines(),
            Microsoft.Extensions.Options.Options.Create(options ?? Options()), NullLogger<NewsIndexer>.Instance);

    public static NewsSyncService Sync(FakeArticleSource source, FakeVectorStore store, NewsIndexer? indexer = null) =>
        new(source, store, indexer ?? Indexer(source, store), Pipelines(), TimeProvider.System, NullLogger<NewsSyncService>.Instance);

    public static string Event(string newsId, string action = "updated", int? attempt = null)
    {
        string a = attempt is null ? string.Empty : $",\"attempt\":{attempt}";
        return $"{{\"schema\":\"fishfind.news-event.v1\",\"eventId\":\"{Guid.NewGuid()}\",\"eventType\":\"fishfind.news.article\","
               + $"\"action\":\"{action}\",\"aggregateId\":\"{newsId}\",\"occurredUtc\":\"2026-10-14T00:00:00Z\",\"source\":\"fishfind-frontend\"{a}}}";
    }

    public static string Words(int n, string word = "walleye") => string.Join(' ', Enumerable.Range(0, n).Select(i => $"{word}{i % 7}"));
}
