using Microsoft.Extensions.Options;
using NewsFind.Configuration;
using NewsFind.Embedding;
using NewsFind.Search;

namespace NewsFind.Indexing;

/// <summary>
/// Opens the index once the process is up: creates the Qdrant collection if needed (retrying until Qdrant
/// answers, never crashing the container — the droplets are small and a crash loop starves the neighbours),
/// then runs the startup sync and the periodic one.
///
/// <para>The event consumer waits on <see cref="Ready"/>, so no event is taken off the queue before the
/// collection exists.</para>
/// </summary>
public sealed class IndexBootstrap : BackgroundService
{
    private readonly IVectorStore _store;
    private readonly ITextEmbedder _embedder;
    private readonly NewsSyncService _sync;
    private readonly SyncOptions _options;
    private readonly ILogger<IndexBootstrap> _logger;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IndexBootstrap(
        IVectorStore store, ITextEmbedder embedder, NewsSyncService sync,
        IOptions<NewsFindOptions> options, ILogger<IndexBootstrap> logger)
    {
        _store = store;
        _embedder = embedder;
        _sync = sync;
        _options = options.Value.Sync;
        _logger = logger;
    }

    /// <summary>Completes once the collection exists.</summary>
    public Task Ready => _ready.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await EnsureCollectionAsync(stoppingToken).ConfigureAwait(false);
            _ready.TrySetResult();

            await StartupSyncAsync(stoppingToken).ConfigureAwait(false);

            if (_options.IntervalMinutes <= 0)
            {
                return;
            }

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.IntervalMinutes));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                // TryStart: an admin-triggered run already in progress covers this tick.
                _sync.TryStart(full: false, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _ready.TrySetCanceled(stoppingToken);
        }
    }

    private async Task EnsureCollectionAsync(CancellationToken stoppingToken)
    {
        TimeSpan delay = TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                await _store.EnsureCollectionAsync(_embedder.Dimension, stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException)
            {
                // Wrong vector size: a configuration error that waiting cannot fix.
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Qdrant not reachable yet; retrying in {Seconds}s.", (int)delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
    }

    private async Task StartupSyncAsync(CancellationToken stoppingToken)
    {
        string mode = _options.OnStartup.Trim().ToLowerInvariant();
        bool full;
        switch (mode)
        {
            case "never":
                return;
            case "always":
                full = false;
                break;
            default: // "ifempty"
                ulong points = await _store.CountAsync(stoppingToken).ConfigureAwait(false);
                if (points > 0)
                {
                    _logger.LogInformation("News index has {Points} points; startup sync skipped (OnStartup=IfEmpty).", points);
                    return;
                }
                full = true;
                break;
        }

        try
        {
            await _sync.RunAsync(full, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logged by the sync; the periodic run will try again.
        }
    }
}
