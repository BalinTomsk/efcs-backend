using Microsoft.Extensions.Options;
using NewsIdx.Configuration;
using NewsIdx.Domain;
using NewsIdx.Indexing;
using Prometheus;

namespace NewsIdx.Messaging;

/// <summary>
/// Polls <c>fishfind.newsidx.events</c> and indexes the article each event names. Runs only when the RabbitMQ
/// keys are configured, and only after <see cref="IndexBootstrap.Ready"/>.
///
/// <list type="bullet">
///   <item>A message that is not a <c>fishfind.news-event.v1</c> news event is logged and dropped.</item>
///   <item>A failure (MySQL, model, Qdrant) re-publishes the message with <c>attempt + 1</c> and backs the
///   poll off exponentially up to <see cref="EventOptions.MaxBackoffSeconds"/>; after
///   <see cref="EventOptions.MaxAttempts"/> it is dropped with an error line. The periodic sync then
///   repairs that article, so a dropped event delays the index — it never loses an article for good.</item>
///   <item>Several events for one article in one batch are indexed once.</item>
/// </list>
/// </summary>
public sealed class NewsEventConsumer : BackgroundService
{
    private static readonly Counter Events = Metrics.CreateCounter(
        "newsidx_events_total", "News events taken off the queue, by outcome.", "outcome");

    private readonly IMessageQueue _queue;
    private readonly NewsIndexer _indexer;
    private readonly IndexBootstrap _bootstrap;
    private readonly EventOptions _options;
    private readonly ILogger<NewsEventConsumer> _logger;

    public NewsEventConsumer(
        IMessageQueue queue, NewsIndexer indexer, IndexBootstrap bootstrap,
        IOptions<NewsIdxOptions> options, ILogger<NewsEventConsumer> logger)
    {
        _queue = queue;
        _indexer = indexer;
        _bootstrap = bootstrap;
        _options = options.Value.Events;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _bootstrap.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
            await DeclareAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("News event consumer started. queue={Queue} pollMs={Poll}", _options.Queue, _options.PollMilliseconds);

            TimeSpan idle = TimeSpan.FromMilliseconds(Math.Max(100, _options.PollMilliseconds));
            TimeSpan delay = idle;
            while (!stoppingToken.IsCancellationRequested)
            {
                bool healthy;
                try
                {
                    healthy = await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "News event poll failed. queue={Queue}", _options.Queue);
                    healthy = false;
                }

                delay = healthy ? idle : TimeSpan.FromSeconds(Math.Min(_options.MaxBackoffSeconds, Math.Max(delay.TotalSeconds * 2, 2)));
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>One poll: take a batch and index it. <c>false</c> when anything failed (→ back off).</summary>
    internal async Task<bool> PollOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> bodies = await _queue
            .GetAsync(_options.Queue, Math.Max(1, _options.BatchSize), cancellationToken).ConfigureAwait(false);

        // Parse, drop the unusable, keep one (the highest-attempt) message per article.
        var byArticle = new Dictionary<string, (NewsEvent Event, string Body)>(StringComparer.Ordinal);
        foreach (string body in bodies)
        {
            NewsEvent? evt = NewsEvent.TryParse(body, out string? error);
            if (evt is null)
            {
                Events.WithLabels("rejected").Inc();
                _logger.LogWarning("News event rejected: {Error}", error);
                continue;
            }

            if (!byArticle.TryGetValue(evt.NewsId, out var seen) || evt.Attempt > seen.Event.Attempt)
            {
                byArticle[evt.NewsId] = (evt, body);
            }
        }

        bool healthy = true;
        foreach ((NewsEvent evt, string body) in byArticle.Values)
        {
            try
            {
                await _indexer.IndexAsync(evt.NewsId, cancellationToken).ConfigureAwait(false);
                Events.WithLabels("applied").Inc();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                healthy = false;
                await RetryLaterAsync(evt, body, ex, cancellationToken).ConfigureAwait(false);
            }
        }
        return healthy;
    }

    private async Task RetryLaterAsync(NewsEvent evt, string body, Exception failure, CancellationToken cancellationToken)
    {
        if (evt.Attempt >= _options.MaxAttempts)
        {
            Events.WithLabels("dropped").Inc();
            _logger.LogError(failure,
                "News event dropped after {Attempts} attempts; the periodic sync will repair it. newsId={NewsId} action={Action}",
                evt.Attempt, evt.NewsId, evt.Action);
            return;
        }

        try
        {
            bool routed = await _queue.PublishAsync(_options.Queue, NewsEvent.WithAttempt(body, evt.Attempt + 1), cancellationToken)
                .ConfigureAwait(false);
            Events.WithLabels(routed ? "retried" : "dropped").Inc();
            _logger.LogWarning(failure,
                "News event failed; re-queued={Routed} attempt={Attempt} newsId={NewsId}", routed, evt.Attempt, evt.NewsId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Events.WithLabels("dropped").Inc();
            _logger.LogError(ex, "News event failed and could not be re-queued; the periodic sync will repair it. newsId={NewsId}", evt.NewsId);
        }
    }

    private async Task DeclareAsync(CancellationToken stoppingToken)
    {
        TimeSpan delay = TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                await _queue.DeclareAsync(_options.Queue, stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "RabbitMQ queue declare failed; retrying in {Seconds}s. queue={Queue}",
                    (int)delay.TotalSeconds, _options.Queue);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
    }
}
