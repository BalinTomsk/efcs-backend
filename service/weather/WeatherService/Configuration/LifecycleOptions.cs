namespace WeatherService.Configuration;

/// <summary>
/// Crash-tracking and daily-usage state locations, bound from <c>Weather:Lifecycle</c>. Mirrors the
/// Spring <c>weather.lifecycle.*</c> properties.
/// </summary>
public sealed class LifecycleOptions
{
    public const string SectionName = "Weather:Lifecycle";

    /// <summary>
    /// Directory holding the lifecycle marker, the incidents log, and the API-usage ledger.
    ///
    /// <para>Survives a redeploy only when it sits on a mounted volume; otherwise everything still
    /// works, it just resets whenever the container is recreated.</para>
    /// </summary>
    public string StateDir { get; set; } = "/app/logs/.lifecycle";

    /// <summary>
    /// Structured log file tailed to describe a crash. Must match the Serilog file sink path in
    /// <c>Program.cs</c>, or a detected crash gets no description.
    /// </summary>
    public string LogFile { get; set; } = "logs/weather.log";
}
