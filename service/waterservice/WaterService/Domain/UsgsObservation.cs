namespace WaterService.Domain;

/// <summary>
/// One latest value of one USGS time series, from the Water Data API <c>latest-continuous</c> collection.
/// </summary>
/// <param name="ParameterCode">USGS parameter code, for example <c>00060</c> (streamflow).</param>
/// <param name="StatisticId">USGS statistic code; <c>00011</c> is an instantaneous value. May be <c>null</c>.</param>
/// <param name="Time">Observation time (the API reports UTC).</param>
/// <param name="Value">Value as published (a decimal string).</param>
public sealed record UsgsObservation(
    string ParameterCode,
    string? StatisticId,
    DateTimeOffset Time,
    string Value);
