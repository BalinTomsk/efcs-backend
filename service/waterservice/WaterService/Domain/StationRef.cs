namespace WaterService.Domain;

/// <summary>
/// Lightweight station reference loaded from the <c>vwWaterStation</c> view.
/// </summary>
/// <param name="Mli">Station identifier.</param>
/// <param name="State">Province / state code used in the CSV URL.</param>
/// <param name="Tz">Timezone offset metadata stored with the station.</param>
public sealed record StationRef(string Mli, string State, int Tz);
