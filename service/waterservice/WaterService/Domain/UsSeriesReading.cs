namespace WaterService.Domain;

/// <summary>
/// Parsed USGS time-series payload ready for the legacy stored procedure.
/// </summary>
/// <param name="Name">Variable name, for example <c>Streamflow</c>.</param>
/// <param name="Unit">Variable unit, for example <c>ft^3/s</c>.</param>
/// <param name="XmlDoc">XML payload in the legacy <c>&lt;root&gt;&lt;a d="..." v="..." /&gt;&lt;/root&gt;</c> format.</param>
public sealed record UsSeriesReading(
    string Name,
    string? Unit,
    string XmlDoc);
