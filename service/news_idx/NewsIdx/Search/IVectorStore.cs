using NewsIdx.Domain;

namespace NewsIdx.Search;

/// <summary>The vector index: one point per article chunk. <see cref="QdrantVectorStore"/> in production.</summary>
public interface IVectorStore
{
    /// <summary>Creates the collection and its payload indexes if missing; fails if it exists with another size.</summary>
    Task EnsureCollectionAsync(int dimension, CancellationToken cancellationToken);

    /// <summary>Number of points (chunks) in the collection.</summary>
    Task<ulong> CountAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Makes the index hold exactly <paramref name="chunks"/> for this article: upserts chunk 0..n-1 (fixed
    /// ids, so they overwrite in place) and then deletes any chunk ≥ n left from a longer earlier version.
    /// The article is never absent from search while it is being re-indexed.
    /// </summary>
    Task ReplaceArticleAsync(NewsArticle article, IReadOnlyList<IndexedChunk> chunks, string model, CancellationToken cancellationToken);

    /// <summary>Removes every chunk of an article (a no-op when it was not indexed).</summary>
    Task DeleteArticleAsync(string newsId, CancellationToken cancellationToken);

    /// <summary>Every indexed article and the last-edit time it was indexed at (read from its chunk 0).</summary>
    Task<IReadOnlyDictionary<string, DateTime>> ListIndexedVersionsAsync(CancellationToken cancellationToken);

    /// <summary>Nearest chunks to <paramref name="vector"/>, best first.</summary>
    Task<IReadOnlyList<ChunkHit>> SearchAsync(float[] vector, int limit, string? country, float minScore, CancellationToken cancellationToken);
}

/// <summary>One passage and its embedding, ready to store.</summary>
public sealed record IndexedChunk(int Chunk, string Text, float[] Vector);

/// <summary>One matching chunk with the article fields stored beside it.</summary>
public sealed record ChunkHit(
    string NewsId,
    int Chunk,
    float Score,
    string Title,
    string? Source,
    string? Country,
    DateTime StampUtc,
    string? LakeId,
    string Text);
