namespace WaterService.Configuration;

/// <summary>
/// Configurable worker behaviour, bound from the <c>Water:Worker</c> configuration section
/// (see <c>appsettings.json</c>). Mirrors the Spring <c>water.worker.*</c> properties.
/// </summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Water:Worker";

    /// <summary>Whether the recurring cycle is scheduled at all. Disable to run console-only.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Run one full cycle immediately on startup, before the first scheduled tick. Default <c>false</c>
    /// (Java-clone parity — the scheduler otherwise waits for the next cron fire). Useful right after a
    /// deploy so the service starts processing all stations without waiting up to an hour.
    /// </summary>
    public bool RunOnStartup { get; set; } = false;

    /// <summary>Cron expression (6-field, seconds first) — default: top of every hour.</summary>
    public string Cron { get; set; } = "0 0 * * * *";

    /// <summary>Pause inserted between (not after) stations within a pass.</summary>
    public int PauseBetweenStationsMs { get; set; } = 1000;

    /// <summary>TCP connect-establishment timeout for the upstream feeds.</summary>
    public int ConnectTimeoutMs { get; set; } = 15000;

    /// <summary>Response read timeout for the upstream feeds.</summary>
    public int ReadTimeoutMs { get; set; } = 30000;

    /// <summary>Honest, identifying User-Agent sent to the public government feeds.</summary>
    public string UserAgent { get; set; } = "water-station-pusher/10.0.1 (+https://fishfind.info)";
}
