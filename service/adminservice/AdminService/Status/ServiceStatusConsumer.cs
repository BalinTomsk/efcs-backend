using AdminService.Configuration;
using AdminService.Data;
using AdminService.Messaging;
using Microsoft.Extensions.Options;
using Prometheus;

namespace AdminService.Status;

/// <summary>
/// Polls <c>fishfind.servicestatus.events</c> and writes each message to dbo.ServiceStatus.
///
/// <list type="bullet">
///   <item>A message that is not a valid <c>fishfind.service-status.v1</c> is logged and dropped.</item>
///   <item>Per (service, machine) only the newest message of a batch is stored — the procedure keeps the newest
///   anyway, and a restart is read from <c>processStartUtc</c>, which every message carries.</item>
///   <item>A database failure drops the batch and backs the poll off. Nothing is re-published: the next heartbeat
///   (one minute later) carries the same state, and a missed <c>stop</c> still shows as dead once the service
///   goes quiet.</item>
/// </list>
/// </summary>
public sealed class ServiceStatusConsumer : BackgroundService
{
    private static readonly Counter Messages = Metrics.CreateCounter(
        "adminservice_status_messages_total", "Status messages taken off the queue, by outcome.", "outcome");

    private readonly IMessageQueue _queue;
    private readonly IServiceStatusRepository _repository;
    private readonly StatusOptions _options;
    private readonly ILogger<ServiceStatusConsumer> _logger;

    public ServiceStatusConsumer(
        IMessageQueue queue, IServiceStatusRepository repository,
        IOptions<AdminOptions> options, ILogger<ServiceStatusConsumer> logger)
    {
        _queue = queue;
        _repository = repository;
        _options = options.Value.Status;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await DeclareAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("Service status consumer started. queue={Queue} pollMs={Poll}", _options.Queue, _options.PollMilliseconds);

            TimeSpan idle = TimeSpan.FromMilliseconds(Math.Max(500, _options.PollMilliseconds));
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
                    _logger.LogWarning(ex, "Service status poll failed. queue={Queue}", _options.Queue);
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

    /// <summary>One poll: take a batch and store it. <c>false</c> when the database failed (→ back off).</summary>
    internal async Task<bool> PollOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> bodies = await _queue
            .GetAsync(_options.Queue, Math.Max(1, _options.BatchSize), cancellationToken).ConfigureAwait(false);

        var newest = new Dictionary<(string, string), ServiceStatusMessage>();
        foreach (string body in bodies)
        {
            ServiceStatusMessage? message = ServiceStatusMessage.TryParse(body, out string? error);
            if (message is null)
            {
                Messages.WithLabels("rejected").Inc();
                _logger.LogWarning("Service status message rejected: {Error}", error);
                continue;
            }

            var key = (message.Service, message.Machine);
            if (!newest.TryGetValue(key, out ServiceStatusMessage? seen) || message.SentUtc >= seen.SentUtc)
            {
                if (seen is not null)
                {
                    Messages.WithLabels("superseded").Inc();
                }
                newest[key] = message;
            }
            else
            {
                Messages.WithLabels("superseded").Inc();
            }
        }

        foreach (ServiceStatusMessage message in newest.Values)
        {
            try
            {
                bool applied = await _repository.ReportAsync(message, cancellationToken).ConfigureAwait(false);
                Messages.WithLabels(applied ? "applied" : "stale").Inc();
                ServiceStatusStats.Increment();
                if (message.Event != "heartbeat")
                {
                    _logger.LogInformation("Service {Event}: service={Service} machine={Machine} version={Version}",
                        message.Event, message.Service, message.Machine, message.Version);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Messages.WithLabels("dropped").Inc();
                _logger.LogWarning(ex, "Service status not stored; the next heartbeat repeats it. service={Service} machine={Machine}",
                    message.Service, message.Machine);
                return false;
            }
        }
        return true;
    }

    private async Task DeclareAsync(CancellationToken stoppingToken)
    {
        var arguments = new Dictionary<string, object>
        {
            ["x-message-ttl"] = Math.Max(60, _options.MessageTtlSeconds) * 1000,
            ["x-max-length"] = Math.Max(100, _options.MaxQueueLength),
        };

        TimeSpan delay = TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                await _queue.DeclareAsync(_options.Queue, arguments, stoppingToken).ConfigureAwait(false);
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
