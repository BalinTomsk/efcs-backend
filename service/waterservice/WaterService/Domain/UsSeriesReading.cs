namespace WaterService.Domain;

/// <summary>
/// One USGS parameter value for a station, named the way the legacy WaterML feed named it
/// (<see cref="Processing.UsgsParameterMap"/>): envdata's <c>sp_water_save_us</c> converts units by that name/unit pair.
/// </summary>
/// <param name="Name">Legacy parameter name, e.g. <c>Streamflow</c>.</param>
/// <param name="Unit">Legacy unit, e.g. <c>ft^3/s</c>; may be <c>null</c>.</param>
/// <param name="Day">The station's LOCAL day of the observation (one reading per day, as WaterData stored it).</param>
/// <param name="Value">The numeric value, in <paramref name="Unit"/>.</param>
public sealed record UsSeriesReading(
    string Name,
    string? Unit,
    DateOnly Day,
    double Value);
