using System.Text.Json;

namespace CsNews.News;

// The shapes of the news data API (/api/v1/csnews/*). They are docapi's own records (docapi
// NewsController.NewsListItem etc.), so docapi can bind them without a translation layer. JSON is camelCase.

/// <summary>The row order of <c>/list</c>. Chosen by docapi from the caller's role, never by the visitor.</summary>
public enum NewsListOrder
{
    /// <summary>Newest article date first (<c>news_stamp</c>). Users and guests.</summary>
    Date,

    /// <summary>Most recently edited first (<c>COALESCE(edit_stamp, stamp)</c>). Admins.</summary>
    Edited,
}

/// <summary>One row of the latest-news list.</summary>
/// <param name="BlockOrd">0 = the requested country's own news; 1 = Canadian padding up to 100.</param>
public sealed record NewsListItem(long Rn, string NewsId, string? Title, string? Source, string? Stamp,
    string? Flag, bool HasPhoto, int BlockOrd);

/// <summary>One page of the list plus the grand total.</summary>
public sealed record NewsListPage(IReadOnlyList<NewsListItem> Items, long Total, int Offset, int Limit);

/// <summary>One article in the news panel of a water-body or species page.</summary>
public sealed record NewsRefItem(string NewsId, string? Title, string? Source, string? Stamp, string? Country);

/// <summary>A water body's news panel.</summary>
public sealed record NewsLakePage(string LakeId, int Limit, IReadOnlyList<NewsRefItem> Items);

/// <summary>A species' news panel.</summary>
public sealed record NewsFishPage(string FishId, int Limit, IReadOnlyList<NewsRefItem> Items);

/// <summary>One SQL LIKE search, already validated by docapi.</summary>
public sealed record NewsSearchQuery(string Query, IReadOnlyList<string> FishIds, string? Country, int Offset, int Limit);

/// <summary>One search hit. <c>Fishes</c> is always empty: this database has no fish table.</summary>
public sealed record NewsSearchItem(string NewsId, string? Title, string? Source, string? Stamp, string? Country,
    IReadOnlyList<string> Fishes, IReadOnlyList<string> FishIds);

/// <summary>One page of a search; <c>Total</c> is capped at <see cref="INewsStore.SearchCap"/>.</summary>
public sealed record NewsSearchPage(IReadOnlyList<NewsSearchItem> Items, int Total, string Query, int Offset, int Limit);

/// <summary>A published story tagged with a water body (the news half of docapi's <c>/news/near</c>).</summary>
public sealed record TaggedNews(string NewsId, string? Title, string? Source, string? Stamp, string? Country, string LakeId);

/// <summary>The home-page document set: lead articles then right-column items, in display order.</summary>
public sealed record NewsDefaultPage(IReadOnlyList<JsonElement> Items);

/// <summary>One paragraph-photo slot of a write. <c>Data</c> is base64; null on an update keeps the stored photo.</summary>
public sealed record NewsPhotoWrite(string? Data, string? Author, string? Alt)
{
    public static readonly NewsPhotoWrite None = new(null, null, null);
}

/// <summary>
/// One article write (<c>POST /doc</c>, <c>PUT /doc/{id}</c>), parsed and validated by docapi
/// (<c>NewsWriteParser</c>). <c>Stamp</c> is a wall-clock <c>yyyy-MM-ddTHH:mm:ss[.fff]</c> without a zone, or
/// null (insert: now; update: keep). Ids are lower-case GUIDs or null.
/// </summary>
public sealed record NewsWrite(
    string Title,
    string? Author,
    string? AuthorLink,
    string? Source,
    string? SourceLink,
    string? VideoLink,
    string? Paragraph0,
    string? Paragraph1,
    string? Paragraph2,
    string? Country,
    string? Stamp,
    string? LakeId,
    string? Fish1Id,
    string? Fish2Id,
    string? Fish3Id,
    NewsPhotoWrite? Photo0,
    NewsPhotoWrite? Photo1,
    NewsPhotoWrite? Photo2);

/// <summary>Every field the AddNews editor's Submit writes, plus the id being saved.</summary>
public sealed record NewsPublishRequest(
    string NewsId,
    string? Title,
    string? Author,
    string? Source,
    string? SourceLink,
    string? AuthorLink,
    string? Stamp,
    string? VideoLink,
    string? Paragraph0,
    string? Paragraph1,
    string? Paragraph2,
    string? Country,
    string? LakeId,
    string? Fish1Id,
    string? Fish2Id,
    string? Fish3Id);

/// <summary><c>Action</c> is <c>inserted</c> or <c>updated</c>.</summary>
public sealed record NewsPublishResult(string NewsId, string Action);

/// <summary>Body of <c>PUT /admin/{id}/photo/{index}</c>. <c>Data</c> (base64) is required.</summary>
public sealed record NewsPhotoUpdateRequest(string? Data, string? Author, string? Alt);

/// <summary>Whether the id exists, and whether a photo column was written.</summary>
public sealed record NewsPhotoUpdateResult(bool Found, bool Updated);

/// <summary>The id of a created article or draft.</summary>
public sealed record NewsIdResult(string NewsId);

/// <summary>Answer of <c>PUT /doc/{id}</c>.</summary>
public sealed record NewsUpdateResult(string NewsId, bool Found);
