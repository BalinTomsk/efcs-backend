namespace NewsIdx.Domain;

/// <summary>
/// The text of one published article, as read from the MySQL <c>news</c> table. Photos are never part of
/// it: the LONGBLOB columns are never selected (see <see cref="Data.MySqlNewsArticleSource"/>).
/// </summary>
/// <param name="NewsId">Primary key, a GUID string (<c>news.news_id</c>).</param>
/// <param name="LastEditUtc"><c>COALESCE(edit_stamp, stamp)</c> — how the sync decides an article changed.</param>
public sealed record NewsArticle(
    string NewsId,
    string Title,
    string? Author,
    string? Source,
    IReadOnlyList<string> Paragraphs,
    IReadOnlyList<string> PhotoAlts,
    string? Country,
    DateTime StampUtc,
    string? LakeId,
    IReadOnlyList<string> FishIds,
    DateTime LastEditUtc);

/// <summary>A published article's id and last-edit time — the narrow row the sync compares.</summary>
public sealed record NewsVersion(string NewsId, DateTime LastEditUtc);
