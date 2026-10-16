using NewsIdx.Domain;

namespace NewsIdx.Data;

/// <summary>
/// Read access to the articles being indexed. <see cref="MySqlNewsArticleSource"/> when
/// <c>MYSQL_NEWS_URL</c> is set, otherwise <see cref="InMemoryNewsArticleSource"/> (no articles), so the
/// service still starts on a workstation with no database.
/// </summary>
public interface INewsArticleSource
{
    /// <summary>One article if it exists <em>and is published</em>; <c>null</c> otherwise (draft, deleted, unknown).</summary>
    Task<NewsArticle?> GetPublishedAsync(string newsId, CancellationToken cancellationToken = default);

    /// <summary>Id and last-edit time of every published article — the sync's view of the truth.</summary>
    Task<IReadOnlyList<NewsVersion>> ListPublishedVersionsAsync(CancellationToken cancellationToken = default);
}

/// <summary>No-database backing: there are no articles.</summary>
public sealed class InMemoryNewsArticleSource : INewsArticleSource
{
    public Task<NewsArticle?> GetPublishedAsync(string newsId, CancellationToken cancellationToken = default) =>
        Task.FromResult<NewsArticle?>(null);

    public Task<IReadOnlyList<NewsVersion>> ListPublishedVersionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NewsVersion>>([]);
}
