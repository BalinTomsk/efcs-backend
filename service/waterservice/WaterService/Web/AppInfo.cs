using System.Reflection;

namespace WaterService.Web;

/// <summary>Process-wide build/runtime metadata surfaced by the <c>/health</c> endpoint.</summary>
public static class AppInfo
{
    /// <summary>UTC time the process started, used to compute uptime.</summary>
    public static readonly DateTime StartedUtc = DateTime.UtcNow;

    /// <summary>Assembly version (from the csproj <c>&lt;Version&gt;</c>), e.g. <c>1.0.0</c>.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>Seconds since process start.</summary>
    public static long UptimeSeconds => (long)(DateTime.UtcNow - StartedUtc).TotalSeconds;
}
