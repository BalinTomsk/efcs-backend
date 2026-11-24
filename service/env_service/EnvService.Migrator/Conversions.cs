using System.Globalization;
using System.Text.Json;

namespace EnvService.Migrator;

/// <summary>One MSSQL <c>dbo.WaterData</c> row as read (its own units and quirks), joined to <c>WaterStation.sid</c>.</summary>
internal sealed record WaterDataRow(
    int Sid, DateTime Stamp, double? Discharge, double? Elevation, double? Turbidity, double? Oxygen, int? Ph,
    double? Velocity, int? Pressure, double? Precipitation, double? Wind, double? WindDir, double? Humidity,
    double? Phycocyanins, double? Chlorophylls, double? Nitrate, double? Chloride, double? Phycoerythrin,
    double? Salinity);

/// <summary>One MSSQL <c>dbo.weather_Forecast</c> row.</summary>
internal sealed record ForecastRow(
    string Mli, int ProviderType, DateOnly Date, TimeSpan? Time, double? TempHighC, double? TempLowC, double? TempDayC,
    int? TempC, double? GpfDay, double? GpfNight, double? Humidity, double? WindSpeedKmh, double? WindDegrees,
    string? WindDirection, int? PressureHpa, int? Pop, int? RainToday, int? WeatherCode, string? Icon,
    string? ShortText, string? LongText);

/// <summary>One MSSQL <c>dbo.WaterStation</c> row plus whether its water body has species.</summary>
internal sealed record StationRow(
    int Sid, string Mli, Guid Id, string Country, string? State, double Lat, double Lon, int? Tz, int LocType,
    string LocName, Guid? LakeId, string LakeName, int? Elevation, string? Agency, bool HasFish, bool Supported,
    DateTime Stamp);

/// <summary>
/// The unit and quirk conversions from the MSSQL tables to envdata's procedure payloads. Pure functions: every rule is
/// pinned by <c>ConversionsTests</c>.
/// </summary>
internal static class Conversions
{
    public const double HpaToMm = 0.750062;

    /// <summary>
    /// <c>WaterData.pressure</c> holds two units, fixed per station: mm Hg (USGS code 00025, stored as sent before
    /// 2026-11-13) and hPa (converted from 2026-11-13, and the "uncorrected, kPa" stations x 10 before that). It is
    /// STATION pressure, so a high gauge reads low: Utah's 09315000 reads 845..885 hPa. mm Hg never exceeds ~800 (the
    /// sea-level record is ~795 mm), so &gt; 800 = hPa, 450..800 = mm Hg, anything else is not a pressure (local data
    /// 2026-11-25: 704..716 mm, 845..885 and ~1,013 hPa). An hPa gauge above ~2 km (&lt; 800 hPa) would be misread as
    /// mm; USGS has no such barometer in the data. Rounded to 0.1 mm.
    /// </summary>
    public static double? PressureMm(int? raw) => raw switch
    {
        null => null,
        > 800 and < 1100 => Math.Round(raw.Value * HpaToMm, 1, MidpointRounding.AwayFromZero),
        >= 450 and <= 800 => raw.Value,
        _ => null,
    };

    /// <summary><c>-999</c> is the CA source's "no reading" marker (MSSQL compared <c>CAST(x AS int) = -999</c>).</summary>
    public static double? NoReading(double? value) =>
        value is double v && (Math.Truncate(v) == -999 || !double.IsFinite(v)) ? null : value;

    /// <summary>
    /// The <c>sp_water_backfill</c> element for one WaterData row, or <c>null</c> when nothing would be stored.
    /// pH is dropped on purpose: CA never stored it and US stored it truncated to an integer (7.62 → 7), so a copy
    /// would be wrong; fresh readings bring the real value.
    /// </summary>
    public static Dictionary<string, object>? WaterBackfillRow(WaterDataRow r)
    {
        var row = new Dictionary<string, object>
        {
            ["sid"] = r.Sid,
            ["ts"] = r.Stamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };
        void Put(string name, double? value)
        {
            if (value is double v && double.IsFinite(v))
            {
                row[name] = v;
            }
        }

        Put("discharge", NoReading(r.Discharge));
        Put("elevation", NoReading(r.Elevation));
        Put("turbidity", r.Turbidity);
        Put("oxygen", r.Oxygen);
        Put("velocity", r.Velocity);
        Put("pressure_mm", PressureMm(r.Pressure));
        Put("precipitation", r.Precipitation);
        Put("wind", r.Wind);
        Put("winddir", r.WindDir);
        Put("humidity", r.Humidity);
        Put("phycocyanins", r.Phycocyanins);
        Put("chlorophylls", r.Chlorophylls);
        Put("nitrate", r.Nitrate);
        Put("chloride", r.Chloride);
        Put("phycoerythrin", r.Phycoerythrin);
        Put("salinity", r.Salinity);
        return row.Count > 2 ? row : null;
    }

    /// <summary>
    /// The canonical envelope (<c>fishfind.weather.forecast/v1</c>) for one station's MSSQL forecast rows, so
    /// <c>sp_weather_save</c> shreds it exactly as it shreds a live payload (pressureHpa → mm Hg there).
    /// <c>raw</c> marks the source. Absent values are omitted, as the weather service's serializer does.
    /// </summary>
    public static string ForecastEnvelope(string mli, int providerType, IEnumerable<ForecastRow> days, DateTimeOffset fetchedUtc)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("schema", "fishfind.weather.forecast/v1");
            json.WriteString("provider", "mssql-backfill");
            json.WriteNumber("providerType", providerType);
            json.WriteString("mli", mli);
            json.WriteString("fetchedUtc", fetchedUtc);
            json.WriteStartArray("days");
            foreach (ForecastRow d in days)
            {
                json.WriteStartObject();
                json.WriteString("date", d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                json.WriteString("time", (d.Time ?? TimeSpan.Zero).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
                Num(json, "tempHighC", d.TempHighC);
                Num(json, "tempLowC", d.TempLowC);
                Num(json, "tempC", d.TempC);
                Num(json, "tempDayC", d.TempDayC);
                Num(json, "humidityPct", d.Humidity);
                Num(json, "windSpeedKmh", d.WindSpeedKmh);
                Num(json, "windDegrees", d.WindDegrees);
                Str(json, "windDirection", d.WindDirection);
                Num(json, "pressureHpa", d.PressureHpa);
                Num(json, "precipChancePct", d.Pop);
                Num(json, "precipMm", d.RainToday);
                Num(json, "precipDayMm", d.GpfDay);
                Num(json, "precipNightMm", d.GpfNight);
                Num(json, "weatherCode", d.WeatherCode);
                Str(json, "icon", d.Icon);
                Str(json, "conditionsShort", d.ShortText);
                Str(json, "conditionsLong", d.LongText);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartObject("raw");
            json.WriteString("source", "MSSQL dbo.weather_Forecast via EnvService.Migrator");
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The <c>sp_station_mirror</c> element for one WaterStation row.</summary>
    public static Dictionary<string, object?> StationMirrorRow(StationRow s) => new()
    {
        ["sid"] = s.Sid,
        ["mli"] = s.Mli,
        ["id"] = s.Id.ToString(),
        ["country"] = s.Country.Trim(),
        ["state"] = string.IsNullOrWhiteSpace(s.State) ? null : s.State.Trim(),
        ["lat"] = s.Lat,
        ["lon"] = s.Lon,
        ["tz"] = s.Tz,
        ["locType"] = s.LocType,
        ["locName"] = Cut(s.LocName, 255),
        ["lakeId"] = s.LakeId?.ToString(),
        ["lakeName"] = Cut(s.LakeName, 64),
        ["elevation"] = s.Elevation,
        ["agency"] = s.Agency is null ? null : Cut(s.Agency, 128),
        ["hasFish"] = s.HasFish ? 1 : 0,
        ["supported"] = s.Supported ? 1 : 0,
        ["stamp"] = s.Stamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
    };

    private static string Cut(string value, int max) => value.Length > max ? value[..max] : value;

    private static void Num(Utf8JsonWriter json, string name, double? value)
    {
        if (value is double v && double.IsFinite(v))
        {
            json.WriteNumber(name, v);
        }
    }

    private static void Num(Utf8JsonWriter json, string name, int? value)
    {
        if (value is int v)
        {
            json.WriteNumber(name, v);
        }
    }

    private static void Str(Utf8JsonWriter json, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            json.WriteString(name, value);
        }
    }
}
