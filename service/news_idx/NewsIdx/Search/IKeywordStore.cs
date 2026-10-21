using NewsIdx.Domain;

namespace NewsIdx.Search;

/// <summary>
/// The BM25 index: one point per article (not per chunk: BM25 already normalizes for length) holding a sparse
/// term vector. <see cref="QdrantKeywordStore"/> in production, in its own collection next to the vectors.
/// </summary>
public interface IKeywordStore
{
    /// <summary>Creates the collection (sparse vector with Qdrant's IDF modifier) and its payload indexes if missing.</summary>
    Task EnsureCollectionAsync(CancellationToken cancellationToken);

    /// <summary>Number of points (articles) in the collection.</summary>
    Task<ulong> CountAsync(CancellationToken cancellationToken);

    /// <summary>Upserts the article's single point (fixed id, so it overwrites in place).</summary>
    Task ReplaceArticleAsync(NewsArticle article, string text, SparseTerms terms, CancellationToken cancellationToken);

    /// <summary>Removes the article (a no-op when it was not indexed).</summary>
    Task DeleteArticleAsync(string newsId, CancellationToken cancellationToken);

    /// <summary>Every indexed article and the last-edit time it was indexed at.</summary>
    Task<IReadOnlyDictionary<string, DateTime>> ListIndexedVersionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The stored sparse indices (sorted) of each article that is in the BM25 index; an article that is not is left
    /// out of the answer. Used to tell which query words an article lacks, whichever engine found it.
    /// </summary>
    Task<IReadOnlyDictionary<string, uint[]>> GetTermsAsync(IReadOnlyCollection<string> newsIds, CancellationToken cancellationToken);

    /// <summary>Best BM25 matches for <paramref name="query"/>, best first.</summary>
    Task<IReadOnlyList<KeywordHit>> SearchAsync(SparseTerms query, int limit, string? country, CancellationToken cancellationToken);
}

/// <summary>One matching article; <c>Score</c> is its BM25 score (unbounded, higher = better).</summary>
public sealed record KeywordHit(
    string NewsId,
    float Score,
    string Title,
    string? Source,
    string? Country,
    DateTime StampUtc,
    string? LakeId,
    string Text);
