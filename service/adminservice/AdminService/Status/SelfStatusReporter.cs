using AdminService.Configuration;
using AdminService.Data;
using AdminService.Web;
using Microsoft.Extensions.Options;

namespace AdminService.Status;

/// <summary>
/// Writes adminservice's own row straight to the database (start, a heartbeat every
/// <see cref="StatusOptions.HeartbeatSeconds"/>, stop), so the status page also shows when the collector itself
/// is down. Its <c>lastAction</c> is the number of messages it has stored.
/// </summary>
public sealed class SelfStatusReporter : BackgroundService
{
    public const string ServiceName = "adminservice";

    private readonly IServiceStatusRepository _repository;
    private readonly StatusOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SelfStatusReporter> _logger;
    private readonly string _machine;

    public SelfStatusReporter(
        IServiceStatusRepository repository, IOptions<AdminOptions> options, TimeProvider time,
        IConfiguration configuration, ILogger<SelfStatusReporter> logger)
    {
        _repository = repository;
        _options = options.Value.Status;
        _time = time;
        _logger = logger;
        _machine = MachineName(configuration["STATUS_MACHINE"] ?? _options.Machine);
    }

    internal static string MachineName(string? configured) =>
        (string.IsNullOrWhiteSpace(configured) ? Environment.MachineName : configured).Trim().ToLowerInvariant();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string evt = "start";
        var interval = TimeSpan.FromSeconds(Math.Max(10, _options.HeartbeatSeconds));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ReportAsync(evt, stoppingToken).ConfigureAwait(false);
                evt = "heartbeat";
                await Task.Delay(interval, _time, stoppingToken).ConfigureAwait(false);
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
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await ReportAsync("stop", timeout.Token).ConfigureAwait(false);
    }

    private async Task ReportAsync(string evt, CancellationToken cancellationToken)
    {
        DateTime now = _time.GetUtcNow().UtcDateTime;
        var message = new ServiceStatusMessage(evt, ServiceName, _machine, AppInfo.Version, AppInfo.StartedUtc, now,
            now, $"{ServiceStatusStats.Stored} status messages stored");
        try
        {
            await _repository.ReportAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Own status not stored. event={Event}", evt);
        }
    }
}

/// <summary>Counter shared by the consumer and the self reporter.</summary>
internal static class ServiceStatusStats
{
    private static long _stored;

    public static long Stored => Interlocked.Read(ref _stored);

    public static void Increment() => Interlocked.Increment(ref _stored);
}
