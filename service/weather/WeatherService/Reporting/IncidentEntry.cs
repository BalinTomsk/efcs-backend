namespace WeatherService.Reporting;

/// <summary>One detected unclean-shutdown (crash) incident, as recorded for the weekly report email.</summary>
/// <param name="DetectedAt">When the next startup noticed the previous run had not shut down cleanly.</param>
/// <param name="DowntimeStart">Last sign of life in the previous run's log.</param>
/// <param name="DowntimeEnd">When the service came back — equal to <paramref name="DetectedAt"/>.</param>
/// <param name="Description">Last ERROR/WARN line before the gap, or why none was available.</param>
public sealed record IncidentEntry(
    DateTime DetectedAt,
    DateTime DowntimeStart,
    DateTime DowntimeEnd,
    string Description);
