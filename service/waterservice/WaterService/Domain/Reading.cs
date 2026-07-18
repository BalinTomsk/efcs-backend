namespace WaterService.Domain;

/// <summary>
/// Parsed hydrometric reading for a station timestamp.
/// </summary>
/// <param name="StationId">Source station identifier from the CSV payload.</param>
/// <param name="Stamp">Reading timestamp with offset information.</param>
/// <param name="WaterLevel">Water level value mapped to the legacy <c>elevation</c> column.</param>
/// <param name="Discharge">Discharge value from the CSV payload.</param>
public sealed record Reading(
    string StationId,
    DateTimeOffset Stamp,
    double? WaterLevel,
    double? Discharge);
