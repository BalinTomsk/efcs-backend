using System.Text.Json;

namespace CsNews.News;

/// <summary>
/// Every read and write of the MySQL <c>news</c> table (Winhost). Since csnews-env 0.6.0 this is the only code
/// on the platform that talks to that database; docapi reaches it through <c>/api/v1/csnews/*</c>.
/// </summary>
public interface INewsStore
{
    /// <summary>
    /// Hard cap on how many matches a search considers before offset/limit page them — the old
    /// <c>dbo.fn_news_search</c>'s <c>TOP 100</c>, kept so a pager total means the same thing it always did.
    /// </summary>
    const int SearchCap = 100;

    /// <summary><c>sp_news_doc_get</c>: the article as a JSON document, or null when the id is unknown.</summary>
    Task<JsonElement?> GetDocumentAsync(string id, CancellationToken ct);

    /// <summary><c>sp_news_doc_export</c>: the interchange document (drafts too), or null.</summary>
    Task<JsonElement?> ExportAsync(string id, CancellationToken ct);

    /// <summary><c>sp_news_list_json</c>: one page of the latest news.</summary>
    Task<NewsListPage> ListAsync(string? country, int offset, int limit, NewsListOrder order, CancellationToken ct);

    /// <summary>The home-page document set (the <c>v_news_default_doc</c> body, inlined).</summary>
    Task<NewsDefaultPage> DefaultAsync(CancellationToken ct);

    /// <summary>The lead photo of one <em>published</em> article, or null.</summary>
    Task<byte[]?> PhotoAsync(string id, CancellationToken ct);

    /// <summary>SQL LIKE search over published news.</summary>
    Task<NewsSearchPage> SearchAsync(NewsSearchQuery query, CancellationToken ct);

    /// <summary>The latest published articles tagged with one water body, newest first.</summary>
    Task<NewsLakePage> LakeAsync(string lakeId, int limit, CancellationToken ct);

    /// <summary>The latest published articles tagged with one species, newest first.</summary>
    Task<NewsFishPage> FishAsync(string fishId, int limit, CancellationToken ct);

    /// <summary>The newest published stories that carry a water body, newest first.</summary>
    Task<IReadOnlyList<TaggedNews>> TaggedAsync(int limit, CancellationToken ct);

    /// <summary><c>sp_news_doc_insert</c>: one new published article; returns its id.</summary>
    Task<string> InsertAsync(NewsWrite write, CancellationToken ct);

    /// <summary><c>sp_news_doc_update</c>: false when the id is unknown (nothing written).</summary>
    Task<bool> UpdateAsync(string id, NewsWrite write, CancellationToken ct);

    /// <summary><c>sp_news_admin_draft_create</c>: purge unpublished drafts, create one; returns its id.</summary>
    Task<string> CreateDraftAsync(CancellationToken ct);

    /// <summary><c>sp_news_admin_publish</c>: upsert by id and mark published.</summary>
    Task<NewsPublishResult> PublishAsync(NewsPublishRequest request, CancellationToken ct);

    /// <summary><c>sp_news_admin_photo_update</c>: replace one photo slot (0..2).</summary>
    Task<NewsPhotoUpdateResult> UpdatePhotoAsync(string id, int index, byte[] photo, string? author, string? alt,
        CancellationToken ct);

    /// <summary>A <c>SELECT 1</c> round trip; throws when the database is unreachable.</summary>
    Task PingAsync(CancellationToken ct);
}

/// <summary>The news database is down, slow, behind an open breaker, or not configured → 503.</summary>
public sealed class NewsStoreUnavailableException : Exception
{
    public NewsStoreUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>Bad input on the news data API → 400.</summary>
public sealed class NewsRequestException : Exception
{
    public NewsRequestException(string message) : base(message)
    {
    }
}

/// <summary>An unknown id on the news data API → 404.</summary>
public sealed class NewsNotFoundException : Exception
{
    public NewsNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>Stands in when <c>MYSQL_NEWS_URL</c> is not set: every call is a 503, search keeps working.</summary>
public sealed class UnconfiguredNewsStore : INewsStore
{
    private static NewsStoreUnavailableException Off() => new("The news database is not configured (MYSQL_NEWS_URL)");

    public Task<JsonElement?> GetDocumentAsync(string id, CancellationToken ct) => throw Off();
    public Task<JsonElement?> ExportAsync(string id, CancellationToken ct) => throw Off();
    public Task<NewsListPage> ListAsync(string? country, int offset, int limit, NewsListOrder order, CancellationToken ct) => throw Off();
    public Task<NewsDefaultPage> DefaultAsync(CancellationToken ct) => throw Off();
    public Task<byte[]?> PhotoAsync(string id, CancellationToken ct) => throw Off();
    public Task<NewsSearchPage> SearchAsync(NewsSearchQuery query, CancellationToken ct) => throw Off();
    public Task<NewsLakePage> LakeAsync(string lakeId, int limit, CancellationToken ct) => throw Off();
    public Task<NewsFishPage> FishAsync(string fishId, int limit, CancellationToken ct) => throw Off();
    public Task<IReadOnlyList<TaggedNews>> TaggedAsync(int limit, CancellationToken ct) => throw Off();
    public Task<string> InsertAsync(NewsWrite write, CancellationToken ct) => throw Off();
    public Task<bool> UpdateAsync(string id, NewsWrite write, CancellationToken ct) => throw Off();
    public Task<string> CreateDraftAsync(CancellationToken ct) => throw Off();
    public Task<NewsPublishResult> PublishAsync(NewsPublishRequest request, CancellationToken ct) => throw Off();
    public Task<NewsPhotoUpdateResult> UpdatePhotoAsync(string id, int index, byte[] photo, string? author, string? alt, CancellationToken ct) => throw Off();
    public Task PingAsync(CancellationToken ct) => throw Off();
}
