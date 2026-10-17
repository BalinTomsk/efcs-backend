namespace AdminService.Configuration;

/// <summary>Bound from the <c>AdminService</c> configuration section (<c>AdminService__Status__…</c> in the env file).</summary>
public sealed class AdminOptions
{
    public const string SectionName = "AdminService";

    public StatusOptions Status { get; set; } = new();
}

public sealed class StatusOptions
{
    /// <summary>Queue every service publishes its status messages to. Declared by this service only.</summary>
    public string Queue { get; set; } = "fishfind.servicestatus.events";

    public int PollMilliseconds { get; set; } = 5000;
    public int BatchSize { get; set; } = 100;
    public int MaxBackoffSeconds { get; set; } = 120;

    /// <summary>
    /// Messages older than this are dropped by RabbitMQ (<c>x-message-ttl</c>), so a long adminservice outage
    /// leaves no backlog: a heartbeat is worthless once the next one is due.
    /// </summary>
    public int MessageTtlSeconds { get; set; } = 600;

    /// <summary>Cap on the queue (<c>x-max-length</c>, oldest dropped first).</summary>
    public int MaxQueueLength { get; set; } = 10_000;

    /// <summary>How often adminservice writes its own heartbeat row (it does not go through the queue).</summary>
    public int HeartbeatSeconds { get; set; } = 60;

    /// <summary>
    /// Name this host reports as in dbo.ServiceStatus. Inside Docker <c>Environment.MachineName</c> is the
    /// container id, so the deploy sets <c>STATUS_MACHINE</c> (e.g. <c>csnode</c>); this is the fallback.
    /// </summary>
    public string? Machine { get; set; }
}
