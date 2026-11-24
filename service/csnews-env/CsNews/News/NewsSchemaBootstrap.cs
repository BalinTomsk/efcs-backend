using MySqlConnector;

namespace CsNews.News;

/// <summary>
/// Best-effort schema fixes the app account (<c>portos</c>) can make itself, run once at startup. Moved from
/// docapi's <c>NewsIndexBootstrap</c> + <c>NewsPhotoTriggerBootstrap</c> (docapi 1.44.0 has no MySQL access).
/// <list type="bullet">
///   <item><c>news.idx_news_publish</c>: <c>sp_news_admin_draft_create</c>'s <c>DELETE ... WHERE news_publish &lt;&gt; 1</c>
///   was a full scan that hangs on this host (confirmed 2026-09-15). Created only when missing.</item>
///   <item><c>TR_news_has_photo0_ins/_upd</c>: keep <c>has_photo0</c> in step with <c>news_photo0</c> (they were never
///   applied live, which hid new leads from the home page). Dropped and re-created every start, so a body
///   change here always reaches the database.</item>
/// </list>
/// Source of truth for both: <c>envfish-db/mysql/script01_createTable.sql</c>. Never load-bearing: a failure is
/// one warning and the service starts anyway. Runs in the background so a slow database never delays startup.
/// </summary>
public sealed class NewsSchemaBootstrap : BackgroundService
{
    internal const string IndexCheckSql =
        "SELECT COUNT(*) FROM information_schema.statistics "
        + "WHERE table_schema = DATABASE() AND table_name = 'news' AND index_name = 'idx_news_publish'";
    internal const string IndexCreateSql = "CREATE INDEX idx_news_publish ON news (news_publish)";

    internal static readonly string[] TriggerSql =
    [
        "DROP TRIGGER IF EXISTS TR_news_has_photo0_ins",
        "CREATE TRIGGER TR_news_has_photo0_ins BEFORE INSERT ON news "
            + "FOR EACH ROW SET NEW.has_photo0 = (NEW.news_photo0 IS NOT NULL)",
        "DROP TRIGGER IF EXISTS TR_news_has_photo0_upd",
        "CREATE TRIGGER TR_news_has_photo0_upd BEFORE UPDATE ON news "
            + "FOR EACH ROW SET NEW.has_photo0 = (NEW.news_photo0 IS NOT NULL)",
    ];

    private readonly string _connectionString;
    private readonly ILogger<NewsSchemaBootstrap> _logger;

    public NewsSchemaBootstrap(string connectionString, ILogger<NewsSchemaBootstrap> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(stoppingToken).ConfigureAwait(false);

            await using (var check = new MySqlCommand(IndexCheckSql, connection))
            {
                long count = Convert.ToInt64(await check.ExecuteScalarAsync(stoppingToken).ConfigureAwait(false));
                if (count > 0)
                {
                    _logger.LogInformation("news.idx_news_publish already exists, nothing to do");
                }
                else
                {
                    await using var create = new MySqlCommand(IndexCreateSql, connection);
                    await create.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
                    _logger.LogInformation("Created news.idx_news_publish (was missing)");
                }
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not ensure news.idx_news_publish exists; the draft purge keeps scanning the table");
        }

        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(stoppingToken).ConfigureAwait(false);
            foreach (string sql in TriggerSql)
            {
                await using var command = new MySqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
            }
            _logger.LogInformation("news.has_photo0 maintenance triggers (re)created");
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not (re)create the news.has_photo0 triggers; has_photo0 can drift on writes");
        }
    }
}
