using Microsoft.Extensions.Options;
using NewsFind.Configuration;
using NewsFind.Domain;
using Polly;
using Polly.Registry;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using static Qdrant.Client.Grpc.Conditions;

namespace NewsFind.Search;

/// <summary>
/// <see cref="IVectorStore"/> over Qdrant's gRPC API. Every call goes through the <c>qdrant</c> Polly pipeline.
///
/// <para><strong>Point layout.</strong> Id = <see cref="NewsIds.PointId"/>(news_id, chunk); vector = 384-d
/// cosine; payload = <c>news_id</c>, <c>chunk</c>, <c>title</c>, <c>source</c>, <c>country</c>,
/// <c>stamp</c> (ISO date), <c>stamp_unix</c>, <c>last_edit_unix</c>, <c>lake_id</c>, <c>text</c> (the
/// passage, for the snippet), <c>model</c>. Keyword indexes on <c>news_id</c> and <c>country</c>, integer
/// indexes on <c>chunk</c> and <c>stamp_unix</c>.</para>
/// </summary>
public sealed class QdrantVectorStore : IVectorStore
{
    /// <summary>Stored passage text is capped; it only feeds the snippet.</summary>
    internal const int MaxStoredText = 2000;

    private readonly QdrantClient _client;
    private readonly ResiliencePipeline _pipeline;
    private readonly string _collection;
    private readonly ILogger<QdrantVectorStore> _logger;

    public QdrantVectorStore(
        QdrantClient client,
        ResiliencePipelineProvider<string> pipelines,
        IOptions<NewsFindOptions> options,
        ILogger<QdrantVectorStore> logger)
    {
        _client = client;
        _pipeline = pipelines.GetPipeline(ResiliencePipelines.Qdrant);
        _collection = options.Value.Qdrant.Collection;
        _logger = logger;
    }

    public async Task EnsureCollectionAsync(int dimension, CancellationToken cancellationToken)
    {
        bool exists = await Run(ct => new ValueTask<bool>(_client.CollectionExistsAsync(_collection, ct)), cancellationToken);
        if (exists)
        {
            CollectionInfo info = await Run(ct => new ValueTask<CollectionInfo>(_client.GetCollectionInfoAsync(_collection, ct)), cancellationToken);
            ulong size = info.Config.Params.VectorsConfig.Params?.Size ?? 0;
            if (size != (ulong)dimension)
            {
                throw new InvalidOperationException(
                    $"Qdrant collection '{_collection}' has vector size {size}, the model produces {dimension}. "
                    + "Delete the collection (or set NewsFind__Qdrant__Collection) and run a full reindex.");
            }
            return;
        }

        await Run(async ct =>
        {
            await _client.CreateCollectionAsync(_collection,
                new VectorParams { Size = (ulong)dimension, Distance = Distance.Cosine },
                cancellationToken: ct);
            return true;
        }, cancellationToken);

        foreach ((string field, PayloadSchemaType type) in new[]
                 {
                     ("news_id", PayloadSchemaType.Keyword),
                     ("country", PayloadSchemaType.Keyword),
                     ("chunk", PayloadSchemaType.Integer),
                     ("stamp_unix", PayloadSchemaType.Integer),
                 })
        {
            await Run(async ct =>
            {
                await _client.CreatePayloadIndexAsync(_collection, field, type, cancellationToken: ct);
                return true;
            }, cancellationToken);
        }

        _logger.LogInformation("Qdrant collection created. collection={Collection} dimension={Dimension}", _collection, dimension);
    }

    public Task<ulong> CountAsync(CancellationToken cancellationToken) =>
        Run(ct => new ValueTask<ulong>(_client.CountAsync(_collection, exact: true, cancellationToken: ct)), cancellationToken);

    public async Task ReplaceArticleAsync(
        NewsArticle article, IReadOnlyList<IndexedChunk> chunks, string model, CancellationToken cancellationToken)
    {
        string newsId = NewsIds.Normalize(article.NewsId);
        long stampUnix = new DateTimeOffset(article.StampUtc).ToUnixTimeSeconds();
        long editUnix = new DateTimeOffset(article.LastEditUtc).ToUnixTimeSeconds();

        var points = chunks.Select(c =>
        {
            var point = new PointStruct
            {
                Id = NewsIds.PointId(newsId, c.Chunk),
                Vectors = c.Vector,
            };
            point.Payload["news_id"] = newsId;
            point.Payload["chunk"] = c.Chunk;
            point.Payload["title"] = article.Title;
            point.Payload["source"] = article.Source ?? string.Empty;
            point.Payload["country"] = article.Country ?? string.Empty;
            point.Payload["stamp"] = article.StampUtc.ToString("yyyy-MM-dd");
            point.Payload["stamp_unix"] = stampUnix;
            point.Payload["last_edit_unix"] = editUnix;
            point.Payload["lake_id"] = article.LakeId ?? string.Empty;
            point.Payload["text"] = c.Text.Length > MaxStoredText ? c.Text[..MaxStoredText] : c.Text;
            point.Payload["model"] = model;
            return point;
        }).ToList();

        await Run(async ct =>
        {
            await _client.UpsertAsync(_collection, points, wait: true, cancellationToken: ct);
            return true;
        }, cancellationToken);

        // Trim chunks left over from a longer earlier version of the article.
        Filter stale = MatchKeyword("news_id", newsId) & Range("chunk", new Qdrant.Client.Grpc.Range { Gte = chunks.Count });
        await Run(async ct =>
        {
            await _client.DeleteAsync(_collection, stale, wait: true, cancellationToken: ct);
            return true;
        }, cancellationToken);
    }

    public Task DeleteArticleAsync(string newsId, CancellationToken cancellationToken) =>
        Run(async ct =>
        {
            await _client.DeleteAsync(_collection, MatchKeyword("news_id", NewsIds.Normalize(newsId)), wait: true, cancellationToken: ct);
            return true;
        }, cancellationToken);

    public async Task<IReadOnlyDictionary<string, DateTime>> ListIndexedVersionsAsync(CancellationToken cancellationToken)
    {
        var versions = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        Filter firstChunks = Match("chunk", 0);
        var payload = new WithPayloadSelector { Include = new PayloadIncludeSelector { Fields = { "news_id", "last_edit_unix" } } };

        PointId? offset = null;
        do
        {
            PointId? from = offset;
            ScrollResponse page = await Run(ct => new ValueTask<ScrollResponse>(
                _client.ScrollAsync(_collection, firstChunks, limit: 512, offset: from, payloadSelector: payload, cancellationToken: ct)),
                cancellationToken);

            foreach (RetrievedPoint point in page.Result)
            {
                if (point.Payload.TryGetValue("news_id", out Value? id))
                {
                    long unix = point.Payload.TryGetValue("last_edit_unix", out Value? e) ? e.IntegerValue : 0;
                    versions[id.StringValue] = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                }
            }

            offset = page.NextPageOffset;
        }
        while (offset is not null);

        return versions;
    }

    public async Task<IReadOnlyList<ChunkHit>> SearchAsync(
        float[] vector, int limit, string? country, float minScore, CancellationToken cancellationToken)
    {
        Filter? filter = null;
        if (country is not null)
        {
            filter = MatchKeyword("country", country);
        }

        IReadOnlyList<ScoredPoint> points = await Run(ct => new ValueTask<IReadOnlyList<ScoredPoint>>(
            _client.QueryAsync(_collection, query: vector, filter: filter, limit: (ulong)limit,
                scoreThreshold: minScore > 0 ? minScore : null, payloadSelector: true, cancellationToken: ct)),
            cancellationToken);

        return points.Select(p => new ChunkHit(
            Text(p, "news_id"),
            (int)(p.Payload.TryGetValue("chunk", out Value? c) ? c.IntegerValue : 0),
            p.Score,
            Text(p, "title"),
            NullIfEmpty(Text(p, "source")),
            NullIfEmpty(Text(p, "country")),
            DateTimeOffset.FromUnixTimeSeconds(p.Payload.TryGetValue("stamp_unix", out Value? s) ? s.IntegerValue : 0).UtcDateTime,
            NullIfEmpty(Text(p, "lake_id")),
            Text(p, "text"))).ToList();
    }

    /// <summary>Readiness probe: Qdrant answers its health RPC.</summary>
    public async Task PingAsync(CancellationToken cancellationToken) =>
        await _client.HealthAsync(cancellationToken);

    private async Task<T> Run<T>(Func<CancellationToken, ValueTask<T>> call, CancellationToken cancellationToken) =>
        await _pipeline.ExecuteAsync(call, cancellationToken).ConfigureAwait(false);

    private static string Text(ScoredPoint p, string field) =>
        p.Payload.TryGetValue(field, out Value? v) ? v.StringValue : string.Empty;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
