using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NewsIdx.Web;

namespace NewsIdx.Status;

/// <summary>
/// The newest item this process worked on, for the admin status page. Callers record each item as they process
/// it (<c>Record("station 05BH004")</c>); it is cheap — one lock, no I/O — and the reporter sends only the latest
/// one with its next heartbeat, so a busy loop never floods the queue.
/// </summary>
public sealed class ServiceActivity
{
    private readonly object _gate = new();
    private DateTime? _lastUtc;
    private string? _last;

    public void Record(string action)
    {
        lock (_gate)
        {
            _lastUtc = DateTime.UtcNow;
            _last = action;
        }
    }

    public (DateTime? Utc, string? Action) Last
    {
        get
        {
            lock (_gate)
            {
                return (_lastUtc, _last);
            }
        }
    }
}

/// <summary>
/// Publishes this process's status to <c>fishfind.servicestatus.events</c> (RabbitMQ management HTTP API, the
/// platform's usual transport): <c>start</c> once, <c>heartbeat</c> every minute with the latest
/// <see cref="ServiceActivity"/>, <c>stop</c> on a clean shutdown. efcs-backend adminservice stores them in
/// dbo.ServiceStatus; the admin page shows a service as dead once its heartbeats stop.
///
/// Best-effort by design: a broker that is down or slow is logged and skipped, never retried in a loop, and never
/// delays startup or shutdown by more than a few seconds.
/// </summary>
public sealed class ServiceStatusReporter : BackgroundService
{
    public const string MessageType = "fishfind.service-status.v1";
    public const string Queue = "fishfind.servicestatus.events";

    private readonly HttpClient _http;
    private readonly ServiceActivity _activity;
    private readonly ILogger<ServiceStatusReporter> _logger;
    private readonly string _service;
    private readonly string _machine;
    private readonly TimeSpan _interval;
    private bool _warned;

    public ServiceStatusReporter(HttpClient http, ServiceActivity activity, ServiceStatusSettings settings,
        ILogger<ServiceStatusReporter> logger)
    {
        _http = http;
        _activity = activity;
        _logger = logger;
        _service = settings.Service;
        _machine = settings.Machine;
        _interval = settings.Interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string evt = "start";
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PublishAsync(evt, stoppingToken).ConfigureAwait(false);
                evt = "heartbeat";
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown; StopAsync reports the stop.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await PublishAsync("stop", timeout.Token).ConfigureAwait(false);
    }

    internal string Body(string evt, DateTime sentUtc)
    {
        (DateTime? lastUtc, string? last) = _activity.Last;
        return ServiceStatusMessages.Body(evt, _service, _machine, AppInfo.Version, AppInfo.StartedUtc, sentUtc, lastUtc, last);
    }

    private async Task PublishAsync(string evt, CancellationToken cancellationToken)
    {
        try
        {
            await ServiceStatusMessages.PublishAsync(_http, Body(evt, DateTime.UtcNow), cancellationToken).ConfigureAwait(false);
            _warned = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // One warning per outage, not one a minute.
            if (!_warned)
            {
                _logger.LogWarning(ex, "Service status not published. event={Event} queue={Queue}", evt, Queue);
                _warned = true;
            }
        }
    }
}

/// <summary>
/// The <c>fishfind.service-status.v1</c> message and its transport, shared by this process's own reporter and
/// <see cref="QdrantStatusReporter"/>, which reports Qdrant on its behalf.
/// </summary>
public static class ServiceStatusMessages
{
    internal static string Body(string evt, string service, string machine, string version, DateTime processStartUtc,
        DateTime sentUtc, DateTime? lastActionUtc, string? lastAction) =>
        JsonSerializer.Serialize(new
        {
            type = ServiceStatusReporter.MessageType,
            @event = evt,
            service,
            machine,
            version,
            processStartUtc = processStartUtc.ToString("O"),
            sentUtc = sentUtc.ToString("O"),
            lastActionUtc = lastActionUtc?.ToString("O"),
            lastAction,
        });

    /// <summary>Publishes one message; throws on failure (callers log once per outage).</summary>
    internal static async Task PublishAsync(HttpClient http, string payload, CancellationToken cancellationToken)
    {
        var envelope = new
        {
            properties = new { delivery_mode = 1, content_type = "application/json" },
            routing_key = ServiceStatusReporter.Queue,
            payload,
            payload_encoding = "string",
        };
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            "api/exchanges/%2F/amq.default/publish", envelope, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Who this process is, as the status page names it.</summary>
public sealed record ServiceStatusSettings(string Service, string Machine, TimeSpan Interval);

public static class ServiceStatusRegistration
{
    /// <summary>
    /// Floor for <c>STATUS_INTERVAL_SECONDS</c>: a service never reports more often than once a minute, whatever the env
    /// file says, which bounds RabbitMQ traffic. Larger values report less often; then raise the page's
    /// <c>ServiceStatusDeadAfterSeconds</c> to at least 3x the interval, or healthy services read as dead.
    /// </summary>
    public const int MinIntervalSeconds = 60;

    internal static int ReportIntervalSeconds(int configured) => Math.Max(MinIntervalSeconds, configured);

    /// <summary>
    /// Registers <see cref="ServiceActivity"/> always (callers can record unconditionally) and the reporter when
    /// <c>RABBITMQ_MANAGEMENT_URL</c>, <c>RABBITMQ_USERNAME</c> and <c>RABBITMQ_PASSWORD</c> are all set.
    /// <c>STATUS_MACHINE</c> names the host (inside Docker the machine name is the container id).
    /// <c>STATUS_INTERVAL_SECONDS</c> sets the heartbeat period (default and minimum 60).
    /// With <paramref name="qdrantGrpcUrl"/>, also registers <see cref="QdrantStatusReporter"/>, so the page lists
    /// Qdrant (which cannot publish itself) on the same machine. Its REST endpoint is <c>QDRANT_REST_URL</c>, or
    /// the gRPC URL with port 6334 swapped for 6333.
    /// </summary>
    /// <returns><c>true</c> when the reporter runs.</returns>
    public static bool AddServiceStatusReporting(this IServiceCollection services, IConfiguration configuration, string service,
        string? qdrantGrpcUrl = null)
    {
        services.AddSingleton<ServiceActivity>();

        string? url = NullIfBlank(configuration["RABBITMQ_MANAGEMENT_URL"]);
        string? user = NullIfBlank(configuration["RABBITMQ_USERNAME"]);
        string? password = NullIfBlank(configuration["RABBITMQ_PASSWORD"]);
        if (url is null || user is null || password is null)
        {
            return false;
        }

        string machine = (NullIfBlank(configuration["STATUS_MACHINE"]) ?? Environment.MachineName).ToLowerInvariant();
        int seconds = ReportIntervalSeconds(configuration.GetValue("STATUS_INTERVAL_SECONDS", MinIntervalSeconds));
        services.AddSingleton(new ServiceStatusSettings(service, machine, TimeSpan.FromSeconds(seconds)));
        void Broker(HttpClient http)
        {
            http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(5);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        }
        services.AddHttpClient<ServiceStatusReporter>(Broker);
        services.AddHostedService(sp => sp.GetRequiredService<ServiceStatusReporter>());

        if (qdrantGrpcUrl is not null)
        {
            string rest = NullIfBlank(configuration["QDRANT_REST_URL"]) ?? QdrantRestUrl(qdrantGrpcUrl);
            string? apiKey = NullIfBlank(configuration["QDRANT_API_KEY"]);
            services.AddHttpClient(QdrantStatusReporter.QdrantClientName, http =>
            {
                http.BaseAddress = new Uri(rest.TrimEnd('/') + "/");
                http.Timeout = TimeSpan.FromSeconds(5);
                if (apiKey is not null)
                {
                    http.DefaultRequestHeaders.Add("api-key", apiKey);
                }
            });
            services.AddHttpClient(QdrantStatusReporter.BrokerClientName, Broker);
            services.AddHostedService<QdrantStatusReporter>();
        }
        return true;
    }

    /// <summary>Qdrant's REST endpoint next to its gRPC one: same host, port 6334 → 6333.</summary>
    internal static string QdrantRestUrl(string grpcUrl)
    {
        var uri = new UriBuilder(grpcUrl);
        if (uri.Port == 6334)
        {
            uri.Port = 6333;
        }
        return uri.Uri.ToString();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
