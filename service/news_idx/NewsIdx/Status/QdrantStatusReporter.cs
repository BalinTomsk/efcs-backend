using System.Globalization;
using System.Text.Json;

namespace NewsIdx.Status;

/// <summary>
/// Reports Qdrant to the admin status page on its behalf: Qdrant cannot publish to RabbitMQ, and news_idx is its
/// only client. Every interval it reads Qdrant's <c>/telemetry</c> (version + process start) and, while Qdrant
/// answers, publishes a <c>fishfind.service-status.v1</c> message as service <c>qdrant</c> on this machine —
/// <c>start</c> when Qdrant's start time is new, <c>heartbeat</c> otherwise. While Qdrant is down nothing is sent,
/// so the page shows it dead after <c>ServiceStatusDeadAfterSeconds</c>, like any service that stopped reporting.
/// Never <c>stop</c>: news_idx cannot tell a clean Qdrant shutdown from a crash.
///
/// Best-effort, like <see cref="ServiceStatusReporter"/>: one warning per outage, no retry loop.
/// </summary>
public sealed class QdrantStatusReporter(IHttpClientFactory httpFactory, ServiceStatusSettings settings,
    ILogger<QdrantStatusReporter> logger) : BackgroundService
{
    public const string Service = "qdrant";
    internal const string QdrantClientName = "qdrant-telemetry";
    internal const string BrokerClientName = "qdrant-status-broker";

    private DateTime? _lastStartupUtc;
    private bool _qdrantWarned;
    private bool _brokerWarned;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ReportOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(settings.Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task ReportOnceAsync(CancellationToken cancellationToken)
    {
        QdrantTelemetry telemetry;
        try
        {
            using HttpClient qdrant = httpFactory.CreateClient(QdrantClientName);
            string json = await qdrant.GetStringAsync("telemetry?details_level=0", cancellationToken).ConfigureAwait(false);
            telemetry = QdrantTelemetry.Parse(json);
            _qdrantWarned = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (!_qdrantWarned)
            {
                logger.LogWarning(ex, "Qdrant telemetry unavailable; not reporting it to the status page.");
                _qdrantWarned = true;
            }
            return;
        }

        string evt = EventFor(telemetry.StartupUtc);
        try
        {
            using HttpClient broker = httpFactory.CreateClient(BrokerClientName);
            await ServiceStatusMessages.PublishAsync(broker, Body(evt, telemetry, DateTime.UtcNow), cancellationToken).ConfigureAwait(false);
            _lastStartupUtc = telemetry.StartupUtc;
            _brokerWarned = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (!_brokerWarned)
            {
                logger.LogWarning(ex, "Qdrant status not published. event={Event} queue={Queue}", evt, ServiceStatusReporter.Queue);
                _brokerWarned = true;
            }
        }
    }

    /// <summary><c>start</c> for a Qdrant process not reported yet (first run, or Qdrant restarted), else <c>heartbeat</c>.</summary>
    internal string EventFor(DateTime startupUtc) => _lastStartupUtc == startupUtc ? "heartbeat" : "start";

    internal string Body(string evt, QdrantTelemetry telemetry, DateTime sentUtc) =>
        ServiceStatusMessages.Body(evt, Service, settings.Machine, telemetry.Version, telemetry.StartupUtc, sentUtc,
            lastActionUtc: null, lastAction: null);
}

/// <summary>What the status page needs from Qdrant's <c>GET /telemetry</c>: <c>result.app.version</c> and <c>.startup</c>.</summary>
public sealed record QdrantTelemetry(string Version, DateTime StartupUtc)
{
    public static QdrantTelemetry Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement app = doc.RootElement.GetProperty("result").GetProperty("app");
        string version = app.GetProperty("version").GetString() ?? "unknown";
        DateTime startup = DateTime.Parse(app.GetProperty("startup").GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        return new QdrantTelemetry(version, startup);
    }
}
