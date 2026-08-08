namespace WeatherService.Domain;

/// <summary>
/// A weather station scheduled for processing, loaded from <c>dbo.vwWeatherForecastToDay</c>.
/// </summary>
/// <param name="Mli">Monitoring location identifier (also the Weather.gov station id for US stations).</param>
/// <param name="Latitude">Station latitude.</param>
/// <param name="Longitude">Station longitude.</param>
/// <param name="State">Station state / province code.</param>
public sealed record StationRef(string Mli, double Latitude, double Longitude, string State);
