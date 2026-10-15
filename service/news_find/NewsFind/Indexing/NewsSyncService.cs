using NewsFind.Data;
using NewsFind.Configuration;
using NewsFind.Search;
using Polly;
using Polly.Registry;
using Prometheus;

namespace NewsFind.Indexing;

/// <summary>
/// Reconciles the whole index with MySQL — the initial load, the "index every article" option, and the
/// periodic repair of anything the events missed.
///
/// <list type="bullet">
///   <item><strong>Incremental</strong> (<c>full = false</c>): compares each published article's
///   <c>COALESCE(edit_stamp, stamp)</c> with the one stored on its chunk 0, and indexes only articles that
///   are missing or changed.</item>
///   <item><strong>Full</strong> (<c>full = true</c>): re-embeds every published article — needed after a
///   model or chunking change, which the timestamps cannot see.</item>
/// </list>
/// Both remove articles that are indexed but no longer published.
///
/// <para>Single-flight: a second request while one runs is refused (<see cref="TryStart"/> returns
/// <c>false</c>), never queued. One article failing is logged and counted; the run goes on.</para>
///
/// <para><strong>Wipe guard:</strong> if MySQL reports no published articles while the index holds some, the
/// prune step is skipped. An empty answer is far likelier to be a permissions or connection fault than a
/// site with every article deleted, and pruning on it would empty search.</para>
/// </summary>
public class NewsSyncService
{
    private static readonly Counter Runs = Metrics.CreateCounter(
        "newsfind_sync_runs_total", "Index syncs by mode and outcome.", "mode", "outcome");

    private readonly INewsArticleSource _source;
    private readonly IVectorStore _store;
    private readonly NewsIndexer _indexer;
    private readonly ResiliencePipeline _mysql;
    private readonly TimeProvider _time;
    private readonly ILogger<NewsSyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile SyncStatus _status = SyncStatus.Idle;

    public NewsSyncService(
        INewsArticleSource source,
        IVectorStore store,
        NewsIndexer indexer,
        ResiliencePipelineProvider<string> pipelines,
        TimeProvider time,
        ILogger<NewsSyncService> logger)
    {
        _source = source;
        _store = store;
        _indexer = indexer;
        _mysql = pipelines.GetPipeline(ResiliencePipelines.MySql);
        _time = time;
        _logger = logger;
    }

    /// <summary>The current or last run.</summary>
    public SyncStatus Status => _status;

    /// <summary>Starts a sync in the background; <c>false</c> when one is already running.</summary>
    public bool TryStart(bool full, CancellationToken stoppingToken)
    {
        if (!_gate.Wait(0))
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunLockedAsync(full, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Already logged and recorded in Status by RunLockedAsync.
            }
            finally
            {
                _gate.Release();
            }
        }, CancellationToken.None);
        return true;
    }

    /// <summary>Runs a sync and waits for it (startup, the CLI). Waits for a running one to finish first.</summary>
    public async Task<SyncStatus> RunAsync(bool full, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunLockedAsync(full, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncStatus> RunLockedAsync(bool full, CancellationToken cancellationToken)
    {
        string mode = full ? "full" : "incremental";
        var status = new SyncStatus { Running = true, Mode = mode, StartedUtc = _time.GetUtcNow().UtcDateTime };
        _status = status;
        _logger.LogInformation("News index sync started. mode={Mode}", mode);

        try
        {
            IReadOnlyList<Domain.NewsVersion> published = await _mysql
                .ExecuteAsync(async ct => await _source.ListPublishedVersionsAsync(ct).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyDictionary<string, DateTime> indexed = await _store.ListIndexedVersionsAsync(cancellationToken).ConfigureAwait(false);

            status.Published = published.Count;
            var todo = published
                .Where(p => full || !indexed.TryGetValue(p.NewsId, out DateTime at) || at < Truncate(p.LastEditUtc))
                .Select(p => p.NewsId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            status.ToIndex = todo.Count;

            foreach (string newsId in todo)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    IndexOutcome outcome = await _indexer.IndexAsync(newsId, cancellationToken).ConfigureAwait(false);
                    if (outcome.Indexed) status.Indexed++; else status.Removed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    status.Failed++;
                    status.LastError = $"{newsId}: {ex.Message}";
                    _logger.LogWarning(ex, "News article failed to index during sync. newsId={NewsId}", newsId);
                }
            }

            if (published.Count == 0 && indexed.Count > 0)
            {
                status.PruneSkipped = true;
                _logger.LogWarning(
                    "MySQL reported no published news while the index holds {Indexed} articles; prune skipped.", indexed.Count);
            }
            else
            {
                var live = published.Select(p => p.NewsId).ToHashSet(StringComparer.Ordinal);
                foreach (string gone in indexed.Keys.Where(id => !live.Contains(id)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _indexer.RemoveAsync(gone, cancellationToken).ConfigureAwait(false);
                    status.Removed++;
                }
            }

            Runs.WithLabels(mode, status.Failed == 0 ? "success" : "partial").Inc();
            _logger.LogInformation(
                "News index sync finished. mode={Mode} published={Published} indexed={Indexed} removed={Removed} failed={Failed}",
                mode, status.Published, status.Indexed, status.Removed, status.Failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Runs.WithLabels(mode, "failure").Inc();
            status.LastError = ex.Message;
            _logger.LogWarning(ex, "News index sync failed. mode={Mode}", mode);
            throw;
        }
        finally
        {
            status.Running = false;
            status.FinishedUtc = _time.GetUtcNow().UtcDateTime;
        }

        return status;
    }

    // The index stores whole seconds; MySQL's DATETIME(6) carries microseconds.
    private static DateTime Truncate(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
}

/// <summary>Progress of a sync, served by <c>GET /admin/sync</c>.</summary>
public sealed class SyncStatus
{
    public static readonly SyncStatus Idle = new();

    public bool Running { get; set; }
    public string? Mode { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public int Published { get; set; }
    public int ToIndex { get; set; }
    public int Indexed { get; set; }
    public int Removed { get; set; }
    public int Failed { get; set; }
    public bool PruneSkipped { get; set; }
    public string? LastError { get; set; }
}
