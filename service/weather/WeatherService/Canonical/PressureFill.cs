using System.Globalization;
using System.Text.Json.Nodes;

namespace WeatherService.Canonical;

/// <summary>
/// Fills <see cref="ForecastDay.PressureHpa"/> from an Open-Meteo <c>hourly=pressure_msl</c> document,
/// for providers whose forecast carries no pressure (Weather.gov <c>/forecast</c>).
///
/// <para>Per local date the <strong>last</strong> hour is taken, the same sample
/// <see cref="OpenMeteoConverter"/> stores for Open-Meteo stations, so every station's daily pressure
/// series is read at the same time of day. Days that already have a pressure keep it.</para>
///
/// <para>Mirrors <c>com.fishfind.weather.canonical.PressureFill</c>.</para>
/// </summary>
public static class PressureFill
{
    /// <summary>True when at least one day has no pressure.</summary>
    public static bool AnyMissing(CanonicalForecast forecast) =>
        forecast.Days.Any(d => d.PressureHpa is null);

    /// <summary>
    /// Last-hour <c>pressure_msl</c> per local date, rounded to whole hPa. A document without
    /// <c>hourly.time</c> / <c>hourly.pressure_msl</c> yields an empty map, never an exception.
    /// </summary>
    public static IReadOnlyDictionary<DateOnly, int> DailyPressure(string openMeteoJson)
    {
        var latest = new Dictionary<DateOnly, (string Time, int Hpa)>();
        JsonNode? hourly = JsonNode.Parse(openMeteoJson)?["hourly"];
        if (hourly?["time"] is not JsonArray times || hourly["pressure_msl"] is not JsonArray pressures)
        {
            return new Dictionary<DateOnly, int>();
        }

        int count = Math.Min(times.Count, pressures.Count);
        for (int i = 0; i < count; i++)
        {
            string? time = times[i]?.GetValue<string>();
            if (pressures[i] is not JsonValue value || !value.TryGetValue(out double hpa)
                || time is null || time.Length < 10
                || !DateOnly.TryParseExact(time[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateOnly date))
            {
                continue;
            }

            if (!latest.TryGetValue(date, out var seen) || string.CompareOrdinal(time, seen.Time) > 0)
            {
                latest[date] = (time, (int)Math.Round(hpa, MidpointRounding.AwayFromZero));
            }
        }

        return latest.ToDictionary(kv => kv.Key, kv => kv.Value.Hpa);
    }

    /// <summary>Returns <paramref name="forecast"/> with each pressure-less day filled where a value exists.</summary>
    public static CanonicalForecast Fill(CanonicalForecast forecast, IReadOnlyDictionary<DateOnly, int> pressureByDay)
    {
        if (pressureByDay.Count == 0 || !AnyMissing(forecast))
        {
            return forecast;
        }

        List<ForecastDay> days = forecast.Days
            .Select(d => d.PressureHpa is null && pressureByDay.TryGetValue(d.Date, out int hpa)
                ? d with { PressureHpa = hpa }
                : d)
            .ToList();
        return forecast with { Days = days };
    }
}
