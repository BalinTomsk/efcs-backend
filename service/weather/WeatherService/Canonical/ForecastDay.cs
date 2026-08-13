using System.Text.Json.Serialization;

namespace WeatherService.Canonical;

/// <summary>
/// One forecast day in the canonical envelope, already reduced and already metric.
///
/// <para>Every member maps 1:1 onto a <c>dbo.weather_Forecast</c> column, which is what lets
/// <c>dbo.sp_ows_meteo_canonical</c> be <c>OPENJSON … WITH … MERGE</c> and nothing else. Anything that
/// used to be decided in T-SQL — unit conversion, picking one row per day, splitting rainfall between
/// day and night, mapping a provider icon to a weather code — is decided here instead, where it is
/// unit-testable and where a bad payload can throw.</para>
///
/// <para>Property names are pinned with <see cref="JsonPropertyNameAttribute"/> rather than left to a
/// naming policy: they are a contract with the database, not an implementation detail. Nulls are
/// omitted, so a missing reading stays missing rather than becoming a fabricated zero.</para>
///
/// <para>Mirrors <c>com.fishfind.weather.canonical.ForecastDay</c>.</para>
/// </summary>
public sealed record ForecastDay
{
    /// <summary>Daily summaries have no hour of their own; see <see cref="Time"/>.</summary>
    public const string DailySummaryTime = "00:00:00";

    /// <summary>Forecast day, local to the station.</summary>
    [JsonPropertyName("date")]
    public required DateOnly Date { get; init; }

    /// <summary>
    /// Hour the values describe, or <c>00:00:00</c> for a daily summary. Never null:
    /// <c>dbo.fnWeatherForecast</c> selects <c>WHERE tm IS NULL</c>, so a null here would expose these
    /// rows to a caller that no other forecast row reaches.
    /// </summary>
    [JsonPropertyName("time")]
    public required string Time { get; init; }

    /// <summary>Daily maximum, °C.</summary>
    [JsonPropertyName("tempHighC")]
    public double? TempHighC { get; init; }

    /// <summary>Daily minimum, °C.</summary>
    [JsonPropertyName("tempLowC")]
    public double? TempLowC { get; init; }

    /// <summary>Representative temperature, °C, rounded — <c>air_temperature</c>.</summary>
    [JsonPropertyName("tempC")]
    public int? TempC { get; init; }

    /// <summary>Mean daytime temperature, °C — <c>tmDay</c>.</summary>
    [JsonPropertyName("tempDayC")]
    public double? TempDayC { get; init; }

    /// <summary>Relative humidity, %.</summary>
    [JsonPropertyName("humidityPct")]
    public double? HumidityPct { get; init; }

    /// <summary>Wind speed, km/h.</summary>
    [JsonPropertyName("windSpeedKmh")]
    public double? WindSpeedKmh { get; init; }

    /// <summary>Wind direction, degrees.</summary>
    [JsonPropertyName("windDegrees")]
    public double? WindDegrees { get; init; }

    /// <summary>Compass abbreviation derived from <see cref="WindDegrees"/>.</summary>
    [JsonPropertyName("windDirection")]
    public string? WindDirection { get; init; }

    /// <summary>Mean sea-level pressure, hPa (= mb).</summary>
    [JsonPropertyName("pressureHpa")]
    public int? PressureHpa { get; init; }

    /// <summary>Probability of precipitation, %.</summary>
    [JsonPropertyName("precipChancePct")]
    public int? PrecipChancePct { get; init; }

    /// <summary>Total precipitation for the day, mm — <c>rain_today</c>.</summary>
    [JsonPropertyName("precipMm")]
    public int? PrecipMm { get; init; }

    /// <summary>Precipitation falling 06:00–17:59, mm — <c>gpfDay</c>.</summary>
    [JsonPropertyName("precipDayMm")]
    public double? PrecipDayMm { get; init; }

    /// <summary>Precipitation falling outside that window, mm — <c>gpfNight</c>.</summary>
    [JsonPropertyName("precipNightMm")]
    public double? PrecipNightMm { get; init; }

    /// <summary>WMO-style code, the vocabulary already stored by the Open-Meteo path.</summary>
    [JsonPropertyName("weatherCode")]
    public int? WeatherCode { get; init; }

    /// <summary>Icon file name, e.g. <c>om_2.png</c>.</summary>
    [JsonPropertyName("icon")]
    public string? Icon { get; init; }

    /// <summary>Short condition text, ≤ 64 chars.</summary>
    [JsonPropertyName("conditionsShort")]
    public string? ConditionsShort { get; init; }

    /// <summary>Long condition text, ≤ 255 chars.</summary>
    [JsonPropertyName("conditionsLong")]
    public string? ConditionsLong { get; init; }

    /// <summary>Compass abbreviation for a bearing, matching the text the legacy T-SQL branches produced.</summary>
    public static string? Compass(double? degrees)
    {
        if (degrees is null)
        {
            return null;
        }

        double d = degrees.Value % 360;
        if (d < 0)
        {
            d += 360;
        }

        return d switch
        {
            >= 337.5 or < 22.5 => "N",
            < 67.5 => "NE",
            < 112.5 => "E",
            < 157.5 => "SE",
            < 202.5 => "S",
            < 247.5 => "SW",
            < 292.5 => "W",
            _ => "NW"
        };
    }
}
