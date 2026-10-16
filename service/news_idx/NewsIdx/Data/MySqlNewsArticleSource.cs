using MySqlConnector;
using NewsIdx.Domain;

namespace NewsIdx.Data;

/// <summary>
/// Reads articles from the MySQL <c>news</c> table (Winhost) — the same table docapi serves the news pages
/// from. Read-only, plain <c>SELECT</c>s, so the app account (<c>portos</c>, no INSERT/UPDATE) is enough and
/// no database object is created for this service.
///
/// <para><strong>The BLOB hazard is respected.</strong> The Winhost host hangs on any query that touches
/// <c>news_photo0..2</c> (LONGBLOB) while materializing more than one row. Neither statement here names a
/// photo column. The multi-row one (<see cref="VersionsSql"/>) names no LONGTEXT column either and has no
/// ORDER BY, so nothing large enters a sort buffer; article text is read only by primary key, one row at a
/// time (<see cref="ArticleSql"/>) — the access pattern docapi documents as safe.</para>
/// </summary>
public sealed class MySqlNewsArticleSource : INewsArticleSource
{
    internal const string ArticleSql = """
        SELECT news_id, news_title, news_author, news_source,
               news_paragraph0, news_paragraph1, news_paragraph2,
               news_photo_alt0, news_photo_alt1, news_photo_alt2,
               country, news_stamp, lake_id, fish1_id, fish2_id, fish3_id,
               COALESCE(edit_stamp, stamp) AS last_edit
          FROM news
         WHERE news_id = @id AND news_publish = 1
         LIMIT 1
        """;

    internal const string VersionsSql =
        "SELECT news_id, COALESCE(edit_stamp, stamp) AS last_edit FROM news WHERE news_publish = 1";

    private readonly string _connectionString;

    public MySqlNewsArticleSource(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<NewsArticle?> GetPublishedAsync(string newsId, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new MySqlCommand(ArticleSql, connection);
        command.Parameters.AddWithValue("@id", newsId);

        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new NewsArticle(
            NewsIds.Normalize(reader.GetString("news_id")),
            reader.GetString("news_title"),
            Nullable(reader, "news_author"),
            Nullable(reader, "news_source"),
            NonBlank(reader, "news_paragraph0", "news_paragraph1", "news_paragraph2"),
            NonBlank(reader, "news_photo_alt0", "news_photo_alt1", "news_photo_alt2"),
            Nullable(reader, "country")?.ToUpperInvariant(),
            Utc(reader.GetDateTime("news_stamp")),
            Nullable(reader, "lake_id") is { } lake ? NewsIds.Normalize(lake) : null,
            NonBlank(reader, "fish1_id", "fish2_id", "fish3_id").Select(NewsIds.Normalize).ToList(),
            Utc(reader.GetDateTime("last_edit")));
    }

    public async Task<IReadOnlyList<NewsVersion>> ListPublishedVersionsAsync(CancellationToken cancellationToken = default)
    {
        var versions = new List<NewsVersion>(capacity: 4096);

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new MySqlCommand(VersionsSql, connection);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(new NewsVersion(NewsIds.Normalize(reader.GetString(0)), Utc(reader.GetDateTime(1))));
        }

        return versions;
    }

    /// <summary>The connection stays open for at most one statement; used by the readiness probe.</summary>
    public async Task PingAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new MySqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? Nullable(MySqlDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        string value = reader.GetString(ordinal).Trim();
        return value.Length == 0 ? null : value;
    }

    private static List<string> NonBlank(MySqlDataReader reader, params string[] columns) =>
        columns.Select(c => Nullable(reader, c)).OfType<string>().ToList();

    // news_stamp / stamp are DATETIME written in UTC (docapi connects with serverTimezone=UTC).
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
