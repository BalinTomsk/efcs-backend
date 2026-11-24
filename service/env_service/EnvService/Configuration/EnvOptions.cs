namespace EnvService.Configuration;

/// <summary>Job settings, bound from the <c>Env</c> section (env vars <c>Env__MirrorIntervalMinutes</c> …).</summary>
public sealed class EnvOptions
{
    public const string SectionName = "Env";

    /// <summary>Station mirror (SQL Server → envdata): every this many minutes, and once at startup.</summary>
    public int MirrorIntervalMinutes { get; set; } = 60;

    /// <summary>How often the hand-over looks for finished ingest cycles (envdata <c>v_job_run_pending</c>).</summary>
    public int HandOverPollSeconds { get; set; } = 300;

    /// <summary>A hand-over runs at least this often even without a pending cycle (a forecast day moves at midnight).</summary>
    public int HandOverMaxAgeMinutes { get; set; } = 60;

    /// <summary>Rows per table-valued parameter push to SQL Server.</summary>
    public int PushBatchSize { get; set; } = 1000;

    /// <summary>The REST station cache (envdata <c>v_station_info</c>) is reloaded every this many minutes.</summary>
    public int StationCacheMinutes { get; set; } = 15;

    /// <summary>Switches; both on by default. Off = the job never runs on its own (POST /admin/run still works).</summary>
    public bool MirrorEnabled { get; set; } = true;

    public bool HandOverEnabled { get; set; } = true;
}
