namespace WeatherService.Configuration;

/// <summary>
/// Weekly report email settings, bound from <c>Weather:Report</c>. Mirrors the Spring
/// <c>weather.report.*</c> properties.
/// </summary>
public sealed class ReportOptions
{
    public const string SectionName = "Weather:Report";

    /// <summary>Recipient. Blank disables the weekly email entirely (not a startup failure).</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Sender. Blank falls back to <see cref="SmtpOptions.Username"/>.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Six-field cron (seconds first) — default: every Friday at 08:00, server-local time.</summary>
    public string Cron { get; set; } = "0 0 8 * * FRI";
}
