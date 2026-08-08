namespace WeatherService.Reporting;

/// <summary>One completed cycle's summary, as recorded for the weekly report email.</summary>
/// <param name="Date">Day the cycle completed.</param>
/// <param name="Worker">Provider display name, e.g. <c>Weather.gov</c>.</param>
/// <param name="Country">Country the pass covered.</param>
/// <param name="SuccessfulStations">Stations fetched and persisted.</param>
/// <param name="FailedStations">Stations that failed (skips are not failures).</param>
/// <param name="LastProcessedStation">Last station processed successfully, if any.</param>
/// <param name="LastFailedStation">Last station that failed, if any.</param>
public sealed record CycleReportEntry(
    DateOnly Date,
    string Worker,
    string Country,
    int SuccessfulStations,
    int FailedStations,
    string? LastProcessedStation,
    string? LastFailedStation);
