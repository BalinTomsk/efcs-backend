using System.Globalization;
using System.Text;
using System.Text.Json;
using MySqlConnector;

namespace CsNews.News;

/// <summary>
/// The MySQL <c>news</c> table (Winhost). Every statement here moved from docapi 1.43.0
/// (<c>MySqlNewsQueryRepository</c>, <c>MySqlNewsDocumentRepository</c>, <c>MySqlNewsWriteRepository</c>,
/// <c>MySqlNewsAdminCommandRepository</c>, <c>JdbcNewsNearRepository.TAGGED_SQL</c>) unchanged, so the rules
/// learned there still hold:
/// <list type="bullet">
///   <item><strong>The BLOB hazard.</strong> The Winhost host hangs on any query that names
///   <c>news_photo0..2</c> (LONGBLOB) while materializing more than one row. Photo columns are read only by
///   primary key with <c>LIMIT 1</c> (<see cref="PhotoSql"/>, <c>sp_news_doc_export</c>). Multi-row statements
///   select narrow columns only, with no window function — never "simplify" search into one windowed query.</item>
///   <item><strong>The app account (<c>portos</c>) has no INSERT/UPDATE/CREATE ROUTINE.</strong> Writes go through
///   procedures created from the Winhost control panel (definer's rights; <c>envfish-db/mysql/script02_Proc.sql</c>).
///   It does hold ALTER/INDEX/TRIGGER, which <see cref="NewsSchemaBootstrap"/> uses.</item>
///   <item><strong><c>v_news_default_doc</c> does not exist live</strong>, so the home page is its body inlined
///   (<see cref="DefaultSql"/>); keep it in sync with <c>script01_createView.sql</c>.</item>
///   <item>Procedures that run DML before their one result row are read by draining every result set.</item>
/// </list>
/// </summary>
public sealed class MySqlNewsStore : INewsStore
{
    internal const string GetSql = "CALL sp_news_doc_get(@p0)";
    internal const string ExportSql = "CALL sp_news_doc_export(@p0)";
    internal const string ListSql = "CALL sp_news_list_json(@p0, @p1, @p2, @p3)";

    internal const string DefaultSql = """
        SELECT JSON_OBJECT(
                   'news_id', n.news_id,
                   'date', DATE_FORMAT(n.news_stamp, '%Y-%m-%d'),
                   'country', n.country,
                   'flag', IF(n.country IS NULL OR n.country = '', 'empty.gif', CONCAT(n.country, '.png')),
                   'title', n.news_title,
                   'author', n.news_author,
                   'author_link', n.news_author_link,
                   'source', n.news_source,
                   'source_link', n.news_source_link,
                   'credit', n.news_photo_author0,
                   'photo_alt', n.news_photo_alt0,
                   'paragraph0', n.news_paragraph0,
                   'paragraph1', n.news_paragraph1,
                   'lake_id', n.lake_id,
                   'fish1_id', n.fish1_id,
                   'fish2_id', n.fish2_id,
                   'fish3_id', n.fish3_id,
                   'snippet', TRIM(SUBSTRING_INDEX(
                       REPLACE(COALESCE(NULLIF(n.news_paragraph0, ''), n.news_paragraph1, ''), '\r', ''),
                       '\n', 1)),
                   'photo', IF(r.rn <= 2 AND LENGTH(n.news_photo0) > 100, TO_BASE64(n.news_photo0), NULL),
                   'with_photo', IF(r.rn <= 2, TRUE, FALSE)
               ) AS doc
          FROM v_news_default_ranked r
          JOIN news n ON n.news_id = r.news_id
         ORDER BY r.rn
         LIMIT 5
        """;

    /// <summary>Single row by primary key, published only: a draft's photo is never reachable by guessing its id.</summary>
    internal const string PhotoSql = "SELECT news_photo0 FROM news WHERE news_id = @p0 AND news_publish = 1 LIMIT 1";

    /// <summary>Narrow on purpose: the paragraphs are searched, never selected, so they never enter a sort buffer.</summary>
    internal const string SearchSelect =
        "SELECT news_id, news_title AS title, news_source AS source, "
        + "DATE_FORMAT(news_stamp, '%Y-%m-%d') AS stamp, country, fish1_id, fish2_id, fish3_id ";

    /// <summary>
    /// Shared FROM/WHERE of the count and the page. Columns are ORed separately so a headline hit never reads
    /// the LONGTEXT paragraphs. {COUNTRY} and {FISH} are filled per request.
    /// </summary>
    internal const string SearchFromWhere = """
        FROM news
         WHERE news_publish = 1
           {COUNTRY}
           AND (    news_title      LIKE @pat ESCAPE '\\'
                 OR news_source     LIKE @pat ESCAPE '\\'
                 OR news_paragraph0 LIKE @pat ESCAPE '\\'
                 OR news_paragraph1 LIKE @pat ESCAPE '\\'
                 OR news_paragraph2 LIKE @pat ESCAPE '\\'
                 OR news_photo_alt0 LIKE @pat ESCAPE '\\'
                 OR news_photo_alt1 LIKE @pat ESCAPE '\\'
                 OR news_photo_alt2 LIKE @pat ESCAPE '\\'
                 {FISH})
        """;

    internal const string LakeSql = """
        SELECT news_id, news_title AS title, news_source AS source,
               DATE_FORMAT(news_stamp, '%Y-%m-%d') AS stamp, country
          FROM news
         WHERE news_publish = 1
           AND lake_id = @p0
         ORDER BY news_stamp DESC, news_id DESC
         LIMIT @p1
        """;

    internal const string FishSql = """
        SELECT news_id, news_title AS title, news_source AS source,
               DATE_FORMAT(news_stamp, '%Y-%m-%d') AS stamp, country
          FROM news
         WHERE news_publish = 1
           AND (fish1_id = @p0 OR fish2_id = @p0 OR fish3_id = @p0)
         ORDER BY news_stamp DESC, news_id DESC
         LIMIT @p1
        """;

    internal const string TaggedSql = """
        SELECT news_id, news_title AS title, news_source AS source,
               DATE_FORMAT(news_stamp, '%Y-%m-%d') AS stamp, country, lake_id
          FROM news
         WHERE news_publish = 1
           AND lake_id IS NOT NULL
           AND lake_id <> ''
         ORDER BY news_stamp DESC, news_id DESC
         LIMIT @p0
        """;

    internal const string InsertSql = "CALL sp_news_doc_insert(" + Params24 + ")";
    internal const string UpdateSql = "CALL sp_news_doc_update(" + Params19 + ")";
    internal const string DraftCreateSql = "CALL sp_news_admin_draft_create(@new_id); SELECT @new_id";
    internal const string PublishSql = "CALL sp_news_admin_publish(" + Params16 + ")";
    internal const string PhotoUpdateSql = "CALL sp_news_admin_photo_update(@p0, @p1, @p2, @p3, @p4)";

    private const string Params24 = "@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17,@p18,@p19,@p20,@p21,@p22,@p23";
    private const string Params19 = "@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17,@p18";
    private const string Params16 = "@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15";

    private static readonly string[] FishSlots = ["fish1_id", "fish2_id", "fish3_id"];

    private readonly string _connectionString;
    private readonly NewsDbGuard _guard;

    public MySqlNewsStore(string connectionString, NewsDbGuard guard)
    {
        _connectionString = connectionString;
        _guard = guard;
    }

    public Task<JsonElement?> GetDocumentAsync(string id, CancellationToken ct) =>
        _guard.ReadAsync("doc get", t => ScalarJsonAsync(GetSql, [id], t), ct);

    public Task<JsonElement?> ExportAsync(string id, CancellationToken ct) =>
        _guard.ReadAsync("doc export", t => ScalarJsonAsync(ExportSql, [id], t), ct);

    public Task<NewsListPage> ListAsync(string? country, int offset, int limit, NewsListOrder order, CancellationToken ct) =>
        _guard.ReadAsync("list", async t =>
        {
            await using MySqlConnection connection = await OpenAsync(t).ConfigureAwait(false);
            await using MySqlCommand command = Command(connection, ListSql,
                country, offset, limit, order == NewsListOrder.Edited ? "edited" : "date");
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(t).ConfigureAwait(false);
            var items = new List<NewsListItem>();
            long total = 0;
            while (await reader.ReadAsync(t).ConfigureAwait(false))
            {
                if (items.Count == 0)
                {
                    total = Convert.ToInt64(reader["total"], CultureInfo.InvariantCulture);
                }
                items.Add(new NewsListItem(
                    Convert.ToInt64(reader["rn"], CultureInfo.InvariantCulture),
                    Str(reader, "news_id")!,
                    Str(reader, "title"),
                    Str(reader, "source"),
                    Str(reader, "stamp"),
                    Str(reader, "flag"),
                    Bool(reader, "has_photo"),
                    Convert.ToInt32(reader["block_ord"], CultureInfo.InvariantCulture)));
            }
            return new NewsListPage(items, total, offset, limit);
        }, ct);

    public Task<NewsDefaultPage> DefaultAsync(CancellationToken ct) =>
        _guard.ReadAsync("default", async t =>
        {
            await using MySqlConnection connection = await OpenAsync(t).ConfigureAwait(false);
            await using MySqlCommand command = Command(connection, DefaultSql);
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(t).ConfigureAwait(false);
            var items = new List<JsonElement>();
            while (await reader.ReadAsync(t).ConfigureAwait(false))
            {
                string? json = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    items.Add(ParseJson(json));
                }
            }
            return new NewsDefaultPage(items);
        }, ct);

    public Task<byte[]?> PhotoAsync(string id, CancellationToken ct) =>
        _guard.ReadAsync("photo", async t =>
        {
            await using MySqlConnection connection = await OpenAsync(t).ConfigureAwait(false);
            await using MySqlCommand command = Command(connection, PhotoSql, id);
            object? value = await command.ExecuteScalarAsync(t).ConfigureAwait(false);
            return value is byte[] bytes ? bytes : null;
        }, ct);

    public Task<NewsSearchPage> SearchAsync(NewsSearchQuery query, CancellationToken ct) =>
        _guard.ReadAsync("search", async t =>
        {
            IReadOnlyList<string> fishIds = query.FishIds ?? [];
            string where = SearchFromWhere
                .Replace("{COUNTRY}", query.Country is null ? "" : "AND country = @country", StringComparison.Ordinal)
                .Replace("{FISH}", FishClause(fishIds.Count), StringComparison.Ordinal);
            string pattern = "%" + EscapeLike(query.Query) + "%";

            await using MySqlConnection connection = await OpenAsync(t).ConfigureAwait(false);

            int total;
            await using (MySqlCommand count = SearchCommand(connection, "SELECT COUNT(*) " + where, query.Country, pattern, fishIds))
            {
                total = Math.Min(Convert.ToInt32(await count.ExecuteScalarAsync(t).ConfigureAwait(false),
                    CultureInfo.InvariantCulture), INewsStore.SearchCap);
            }

            // A window past the cap is an empty page, never rows 100+.
            int offset = query.Offset;
            int limit = Math.Min(query.Limit, Math.Max(INewsStore.SearchCap - offset, 0));
            if (limit < 1)
            {
                return new NewsSearchPage([], total, query.Query, offset, query.Limit);
            }

            await using MySqlCommand page = SearchCommand(connection,
                SearchSelect + where + " ORDER BY news_stamp DESC, news_id DESC LIMIT @off, @lim",
                query.Country, pattern, fishIds);
            page.Parameters.AddWithValue("@off", offset);
            page.Parameters.AddWithValue("@lim", limit);
            await using MySqlDataReader reader = await page.ExecuteReaderAsync(t).ConfigureAwait(false);
            var items = new List<NewsSearchItem>();
            while (await reader.ReadAsync(t).ConfigureAwait(false))
            {
                items.Add(new NewsSearchItem(
                    Str(reader, "news_id")!,
                    Str(reader, "title"),
                    Str(reader, "source"),
                    Str(reader, "stamp"),
                    Str(reader, "country"),
                    [],
                    DistinctIds(Str(reader, "fish1_id"), Str(reader, "fish2_id"), Str(reader, "fish3_id"))));
            }
            return new NewsSearchPage(items, total, query.Query, offset, query.Limit);
        }, ct);

    public Task<NewsLakePage> LakeAsync(string lakeId, int limit, CancellationToken ct) =>
        _guard.ReadAsync("lake", async t =>
            new NewsLakePage(lakeId, limit, await RefItemsAsync(LakeSql, lakeId, limit, t).ConfigureAwait(false)), ct);

    public Task<NewsFishPage> FishAsync(string fishId, int limit, CancellationToken ct) =>
        _guard.ReadAsync("fish", async t =>
            new NewsFishPage(fishId, limit, await RefItemsAsync(FishSql, fishId, limit, t).ConfigureAwait(false)), ct);

    public Task<IReadOnlyList<TaggedNews>> TaggedAsync(int limit, CancellationToken ct) =>
        _guard.ReadAsync<IReadOnlyList<TaggedNews>>("tagged", async t =>
        {
            await using MySqlConnection connection = await OpenAsync(t).ConfigureAwait(false);
            await using MySqlCommand command = Command(connection, TaggedSql, limit);
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(t).ConfigureAwait(false);
            var items = new List<TaggedNews>();
            while (await reader.ReadAsync(t).ConfigureAwait(false))
            {
                items.Add(new TaggedNews(Str(reader, "news_id")!, Str(reader, "title"), Str(reader, "source"),
                    Str(reader, "stamp"), Str(reader, "country"), Str(reader, "lake_id")!));
            }
            return items;
        }, ct);

    public Task<string> InsertAsync(NewsWrite write, CancellationToken ct) =>
        _guard.WriteAsync("insert", async t =>
        {
            var values = new List<object?>(24);
            AddFields(values, write);
            AddPhoto(values, write.Photo0);
            AddPhoto(values, write.Photo1);
            AddPhoto(values, write.Photo2);
            string? id = await FirstRowAsync(InsertSql, values, r => Str(r, "news_id"), t).ConfigureAwait(false);
            return id ?? throw new InvalidOperationException("sp_news_doc_insert returned no id");
        }, ct);

    public Task<bool> UpdateAsync(string id, NewsWrite write, CancellationToken ct) =>
        _guard.WriteAsync("update", async t =>
        {
            var values = new List<object?>(19) { id };
            AddFields(values, write);
            AddPhoto(values, write.Photo0);
            int? found = await FirstRowAsync(UpdateSql, values,
                r => (int?)Convert.ToInt32(r["found"], CultureInfo.InvariantCulture), t).ConfigureAwait(false);
            return found == 1;
        }, ct);

    public Task<string> CreateDraftAsync(CancellationToken ct) =>
        _guard.WriteAsync("draft create", async t =>
        {
            await using MySqlConnection connection = await OpenAsync(t).ConfigureAwait(false);
            await using MySqlCommand command = Command(connection, DraftCreateSql);
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(t).ConfigureAwait(false);
            string? id = null;
            do
            {
                while (await reader.ReadAsync(t).ConfigureAwait(false))
                {
                    if (id is null && reader.FieldCount > 0 && !reader.IsDBNull(0))
                    {
                        id = reader.GetValue(0) is byte[] raw ? Encoding.UTF8.GetString(raw) : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                    }
                }
            }
            while (await reader.NextResultAsync(t).ConfigureAwait(false));
            return string.IsNullOrWhiteSpace(id)
                ? throw new InvalidOperationException("sp_news_admin_draft_create returned no id")
                : id.Trim();
        }, ct);

    public Task<NewsPublishResult> PublishAsync(NewsPublishRequest r, CancellationToken ct) =>
        _guard.WriteAsync("publish", async t =>
        {
            object?[] values =
            [
                r.NewsId, r.Title, r.Author, r.Source, r.SourceLink, r.AuthorLink, Stamp(r.Stamp), r.VideoLink,
                r.Paragraph0, r.Paragraph1, r.Paragraph2, r.Country, r.LakeId, r.Fish1Id, r.Fish2Id, r.Fish3Id,
            ];
            NewsPublishResult? result = await FirstRowAsync(PublishSql, values,
                row => new NewsPublishResult(Str(row, "news_id")!, Str(row, "action")!), t).ConfigureAwait(false);
            return result ?? throw new InvalidOperationException("sp_news_admin_publish returned no row");
        }, ct);

    public Task<NewsPhotoUpdateResult> UpdatePhotoAsync(string id, int index, byte[] photo, string? author, string? alt,
        CancellationToken ct) =>
        _guard.WriteAsync("photo update", async t =>
        {
            NewsPhotoUpdateResult? result = await FirstRowAsync(PhotoUpdateSql, [id, index, photo, author, alt],
                row => new NewsPhotoUpdateResult(Bool(row, "found"), Bool(row, "updated")), t).ConfigureAwait(false);
            return result ?? new NewsPhotoUpdateResult(false, false);
        }, ct);

    public Task PingAsync(CancellationToken ct) =>
        _guard.ReadAsync("ping", async t =>
        {
            await using MySqlConnection connection = await OpenAsync(t).ConfigureAwait(false);
            await using MySqlCommand command = Command(connection, "SELECT 1");
            return await command.ExecuteScalarAsync(t).ConfigureAwait(false);
        }, ct);

    // ---- helpers --------------------------------------------------------------------------------------------

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A command whose values bind to <c>@p0..@pN</c> in order. null → SQL NULL.</summary>
    private static MySqlCommand Command(MySqlConnection connection, string sql, params object?[] values)
    {
        var command = new MySqlCommand(sql, connection);
        for (int i = 0; i < values.Length; i++)
        {
            command.Parameters.AddWithValue("@p" + i.ToString(CultureInfo.InvariantCulture), values[i] ?? DBNull.Value);
        }
        return command;
    }

    private static MySqlCommand SearchCommand(MySqlConnection connection, string sql, string? country, string pattern,
        IReadOnlyList<string> fishIds)
    {
        var command = new MySqlCommand(sql, connection);
        if (country is not null)
        {
            command.Parameters.AddWithValue("@country", country);
        }
        command.Parameters.AddWithValue("@pat", pattern);
        for (int i = 0; i < fishIds.Count; i++)
        {
            command.Parameters.AddWithValue("@f" + i.ToString(CultureInfo.InvariantCulture), fishIds[i]);
        }
        return command;
    }

    private async Task<JsonElement?> ScalarJsonAsync(string sql, object?[] values, CancellationToken ct)
    {
        string? json = await FirstRowAsync(sql, values, r => r.IsDBNull(0) ? null : r.GetString(0), ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json) ? null : ParseJson(json);
    }

    private async Task<IReadOnlyList<NewsRefItem>> RefItemsAsync(string sql, string id, int limit, CancellationToken ct)
    {
        await using MySqlConnection connection = await OpenAsync(ct).ConfigureAwait(false);
        await using MySqlCommand command = Command(connection, sql, id, limit);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var items = new List<NewsRefItem>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new NewsRefItem(Str(reader, "news_id")!, Str(reader, "title"), Str(reader, "source"),
                Str(reader, "stamp"), Str(reader, "country")));
        }
        return items;
    }

    /// <summary>
    /// Runs a CALL and returns the first row of the first result set that has one, draining every result set
    /// so MySQL can finish the procedure (its DML runs before the SELECT).
    /// </summary>
    private async Task<T?> FirstRowAsync<T>(string sql, IReadOnlyList<object?> values, Func<MySqlDataReader, T?> map,
        CancellationToken ct)
    {
        await using MySqlConnection connection = await OpenAsync(ct).ConfigureAwait(false);
        await using MySqlCommand command = Command(connection, sql, [.. values]);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        T? result = default;
        bool have = false;
        do
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!have)
                {
                    result = map(reader);
                    have = true;
                }
            }
        }
        while (await reader.NextResultAsync(ct).ConfigureAwait(false));
        return result;
    }

    /// <summary>The fifteen shared columns in the procedures' order, title through fish3.</summary>
    private static void AddFields(List<object?> values, NewsWrite w)
    {
        values.AddRange([
            w.Title, w.Author, w.AuthorLink, w.Source, w.SourceLink, w.VideoLink,
            w.Paragraph0, w.Paragraph1, w.Paragraph2, w.Country, Stamp(w.Stamp),
            w.LakeId, w.Fish1Id, w.Fish2Id, w.Fish3Id,
        ]);
    }

    private static void AddPhoto(List<object?> values, NewsPhotoWrite? photo)
    {
        photo ??= NewsPhotoWrite.None;
        values.Add(photo.Data is null ? null : Base64(photo.Data));
        values.Add(photo.Author);
        values.Add(photo.Alt);
    }

    private static readonly string[] StampFormats =
        ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF"];

    /// <summary>A wall-clock stamp (no zone) as a DATETIME parameter; null stays null.</summary>
    internal static DateTime? Stamp(string? stamp)
    {
        if (stamp is null)
        {
            return null;
        }
        return DateTime.TryParseExact(stamp.Trim(), StampFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime value)
            ? value
            : throw new NewsRequestException($"stamp '{stamp}' is not a yyyy-MM-ddTHH:mm:ss date-time");
    }

    internal static byte[] Base64(string data)
    {
        try
        {
            return Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            throw new NewsRequestException("photo data is not valid base64");
        }
    }

    /// <summary>The <c>OR fishN_id IN (...)</c> tail, or nothing — an <c>IN ()</c> is a MySQL syntax error.</summary>
    internal static string FishClause(int idCount)
    {
        if (idCount < 1)
        {
            return string.Empty;
        }
        string placeholders = string.Join(", ", Enumerable.Range(0, idCount).Select(i => "@f" + i.ToString(CultureInfo.InvariantCulture)));
        return string.Join(" ", FishSlots.Select(slot => $"OR {slot} IN ({placeholders})"));
    }

    /// <summary>
    /// Escapes LIKE wildcards so a term matches literally. <c>[</c> is not escaped: MySQL's LIKE has no
    /// character class, and a backslash before it would stop a bracket from matching.
    /// </summary>
    internal static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal)
         .Replace("%", "\\%", StringComparison.Ordinal)
         .Replace("_", "\\_", StringComparison.Ordinal);

    internal static IReadOnlyList<string> DistinctIds(params string?[] ids)
    {
        var output = new List<string>(3);
        foreach (string? id in ids)
        {
            if (!string.IsNullOrWhiteSpace(id) && !output.Contains(id))
            {
                output.Add(id);
            }
        }
        return output;
    }

    private static string? Str(MySqlDataReader reader, string column)
    {
        object value = reader[column];
        return value switch
        {
            DBNull => null,
            string s => s,
            byte[] raw => Encoding.UTF8.GetString(raw),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    private static bool Bool(MySqlDataReader reader, string column)
    {
        object value = reader[column];
        return value switch
        {
            DBNull => false,
            bool b => b,
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
        };
    }

    private static JsonElement ParseJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
