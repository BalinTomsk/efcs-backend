using Microsoft.Extensions.Options;
using NewsIdx.Configuration;
using NewsIdx.Domain;
using Polly;
using Polly.Registry;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using static Qdrant.Client.Grpc.Conditions;

namespace NewsIdx.Search;

/// <summary>
/// <see cref="IKeywordStore"/> over Qdrant. Its own collection (<c>NewsIdx:Bm25:Collection</c>), because Qdrant
/// cannot add a vector to an existing collection and the dense one is live.
///
/// <para><strong>Point layout.</strong> Id = <see cref="NewsIds.PointId"/>(news_id, 0); one sparse vector
/// named <c>bm25</c> with <c>modifier = idf</c> (Qdrant supplies the IDF factor from its live counts); payload
/// = <c>news_id</c>, <c>title</c>, <c>source</c>, <c>country</c>, <c>stamp</c>, <c>stamp_unix</c>,
/// <c>last_edit_unix</c>, <c>lake_id</c>, <c>text</c> (body for the snippet), <c>analyzer</c>.</para>
/// </summary>
public sealed class QdrantKeywordStore : IKeywordStore
{
    public const string VectorName = "bm25";

    private readonly QdrantClient _client;
    private readonly ResiliencePipeline _pipeline;
    private readonly string _collection;
    private readonly ILogger<QdrantKeywordStore> _logger;

    public QdrantKeywordStore(
        QdrantClient client,
        ResiliencePipelineProvider<string> pipelines,
        IOptions<NewsIdxOptions> options,
        ILogger<QdrantKeywordStore> logger)
    {
        _client = client;
        _pipeline = pipelines.GetPipeline(ResiliencePipelines.Qdrant);
        _collection = options.Value.Bm25.Collection;
        _logger = logger;
    }

    public async Task EnsureCollectionAsync(CancellationToken cancellationToken)
    {
        bool exists = await Run(ct => new ValueTask<bool>(_client.CollectionExistsAsync(_collection, ct)), cancellationToken);
        if (exists)
        {
            CollectionInfo info = await Run(ct => new ValueTask<CollectionInfo>(_client.GetCollectionInfoAsync(_collection, ct)), cancellationToken);
            SparseVectorConfig? sparse = info.Config.Params.SparseVectorsConfig;
            if (sparse is null || !sparse.Map.TryGetValue(VectorName, out SparseVectorParams? p) || p.Modifier != Modifier.Idf)
            {
                throw new InvalidOperationException(
                    $"Qdrant collection '{_collection}' has no sparse vector '{VectorName}' with modifier=idf. "
                    + "Delete it (or set NewsIdx__Bm25__Collection) and run a full reindex.");
            }
            return;
        }

        await Run(async ct =>
        {
            await _client.CreateCollectionAsync(_collection,
                new VectorParamsMap(),
                sparseVectorsConfig: new SparseVectorConfig
                {
                    Map = { [VectorName] = new SparseVectorParams { Modifier = Modifier.Idf } },
                },
                cancellationToken: ct);
            return true;
        }, cancellationToken);

        foreach ((string field, PayloadSchemaType type) in new[]
                 {
                     ("news_id", PayloadSchemaType.Keyword),
                     ("country", PayloadSchemaType.Keyword),
                     ("stamp_unix", PayloadSchemaType.Integer),
                 })
        {
            await Run(async ct =>
            {
                await _client.CreatePayloadIndexAsync(_collection, field, type, cancellationToken: ct);
                return true;
            }, cancellationToken);
        }

        _logger.LogInformation("Qdrant BM25 collection created. collection={Collection}", _collection);
    }

    public Task<ulong> CountAsync(CancellationToken cancellationToken) =>
        Run(ct => new ValueTask<ulong>(_client.CountAsync(_collection, exact: true, cancellationToken: ct)), cancellationToken);

    public Task ReplaceArticleAsync(NewsArticle article, string text, SparseTerms terms, CancellationToken cancellationToken)
    {
        string newsId = NewsIds.Normalize(article.NewsId);
        var point = new PointStruct
        {
            Id = NewsIds.PointId(newsId, 0),
            Vectors = new Vectors
            {
                Vectors_ = new NamedVectors { Vectors = { [VectorName] = (terms.Values, terms.Indices) } },
            },
        };
        point.Payload["news_id"] = newsId;
        point.Payload["title"] = article.Title;
        point.Payload["source"] = article.Source ?? string.Empty;
        point.Payload["country"] = article.Country ?? string.Empty;
        point.Payload["stamp"] = article.StampUtc.ToString("yyyy-MM-dd");
        point.Payload["stamp_unix"] = new DateTimeOffset(article.StampUtc).ToUnixTimeSeconds();
        point.Payload["last_edit_unix"] = new DateTimeOffset(article.LastEditUtc).ToUnixTimeSeconds();
        point.Payload["lake_id"] = article.LakeId ?? string.Empty;
        point.Payload["text"] = text.Length > Bm25Encoder.MaxStoredText ? text[..Bm25Encoder.MaxStoredText] : text;
        point.Payload["analyzer"] = Bm25Encoder.Analyzer;

        return Run(async ct =>
        {
            await _client.UpsertAsync(_collection, [point], wait: true, cancellationToken: ct);
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
        var payload = new WithPayloadSelector { Include = new PayloadIncludeSelector { Fields = { "news_id", "last_edit_unix" } } };

        PointId? offset = null;
        do
        {
            PointId? from = offset;
            ScrollResponse page = await Run(ct => new ValueTask<ScrollResponse>(
                _client.ScrollAsync(_collection, filter: null, limit: 512, offset: from, payloadSelector: payload, cancellationToken: ct)),
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

    public async Task<IReadOnlyList<KeywordHit>> SearchAsync(
        SparseTerms query, int limit, string? country, CancellationToken cancellationToken)
    {
        if (query.IsEmpty)
        {
            return [];
        }

        // Not a ternary: Condition → Filter is an implicit conversion that throws on null.
        Filter? filter = null;
        if (country is not null)
        {
            filter = MatchKeyword("country", country);
        }
        IReadOnlyList<ScoredPoint> points = await Run(ct => new ValueTask<IReadOnlyList<ScoredPoint>>(
            _client.QueryAsync(_collection, query: (query.Values, query.Indices), usingVector: VectorName, filter: filter,
                limit: (ulong)limit, payloadSelector: true, cancellationToken: ct)),
            cancellationToken);

        return points.Select(p => new KeywordHit(
            Text(p, "news_id"),
            p.Score,
            Text(p, "title"),
            NullIfEmpty(Text(p, "source")),
            NullIfEmpty(Text(p, "country")),
            DateTimeOffset.FromUnixTimeSeconds(p.Payload.TryGetValue("stamp_unix", out Value? s) ? s.IntegerValue : 0).UtcDateTime,
            NullIfEmpty(Text(p, "lake_id")),
            Text(p, "text"))).ToList();
    }

    private async Task<T> Run<T>(Func<CancellationToken, ValueTask<T>> call, CancellationToken cancellationToken) =>
        await _pipeline.ExecuteAsync(call, cancellationToken).ConfigureAwait(false);

    private static string Text(ScoredPoint p, string field) =>
        p.Payload.TryGetValue(field, out Value? v) ? v.StringValue : string.Empty;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
