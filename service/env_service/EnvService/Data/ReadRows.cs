namespace EnvService.Data;

/// <summary>One row of envdata <c>v_station_info</c>: a station's identity and water-body link. Times are UTC.</summary>
public sealed record StationInfo(
    int Sid, string Mli, string WsId, string Country, string? State, double Lat, double Lon, int? Tz, int LocType,
    string LocName, string? LakeId, string LakeName, int? Elevation, string? Agency, bool HasFish, bool Supported,
    DateTime? UpdData, DateTime? WeatherStamp);

/// <summary>One row of envdata <c>v_water_day</c>: a station's daily means (discharge m³/s, level m, oxygen mg/L).</summary>
public sealed record WaterDayRow(
    int Sid, DateOnly Dt, int Readings, double? Discharge, double? Level, double? Turbidity, double? Oxygen, decimal? Ph);

/// <summary>
/// One row of envdata <c>v_weather_day</c>: a station's stored weather of one day plus that day's pressure state (when
/// <c>sp_fill_pressure_state</c> computed one). Pressure in mm Hg, temperatures in °C, wind in km/h, rain in mm.
/// </summary>
public sealed record WeatherDayRow(
    int Sid, DateOnly Dt, TimeSpan? Tm, double? TmHigh, double? TmLow, double? TmDay, int? AirTemperature,
    double GpfDay, double GpfNight, int? RainToday, int? Pop, double? Humidity, double? WindMaxSpeed, double? WindDegree,
    string? WindDirection, decimal? PressureMm, int? WeatherCode, string? Icon, string? ShortText, string? LongText,
    int ProviderType, DateTime Stamp, string? PressureState, decimal? D1Mm, DateOnly? FrontDt, decimal? SwingMm,
    decimal? Strength);
