using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WeatherService.Canonical;

/// <summary>
/// Converts a weather.gov (NWS) gridpoint forecast into the canonical envelope.
///
/// <para><b>The shape is unlike the other providers': periods, not days.</b> NWS returns
/// <c>properties.periods[]</c>, each covering roughly half a day and flagged <c>isDaytime</c>. A
/// calendar day is therefore assembled from up to two periods — the daytime one carries the day's high,
/// the night one the low — and the first period of a response is frequently a night, because a forecast
/// issued in the evening starts with "Tonight".</para>
///
/// <para>Requested with <c>units=si</c>, so temperatures arrive in °C and wind speeds in km/h and
/// nothing is converted here. <c>windSpeed</c> is still a HUMAN STRING like <c>"10 to 15 km/h"</c>; the
/// upper bound is taken, matching the "daily maximum wind" the other providers report.</para>
///
/// <para>Precipitation AMOUNT is deliberately absent: <c>/forecast</c> publishes only
/// <c>probabilityOfPrecipitation</c>. The quantity lives in the raw <c>/gridpoints</c> document, which
/// this does not fetch, so <c>precipMm</c> stays null rather than being invented — the database
/// defaults the NOT NULL rainfall columns to 0.</para>
///
/// <para>Mirrors <c>com.fishfind.weather.canonical.WeatherGovConverter</c>.</para>
/// </summary>
public partial class WeatherGovConverter : IForecastConverter
{
    /// <summary>NWS icon token → the WMO-style codes the Open-Meteo path already stores.</summary>
    private static readonly Dictionary<string, int> TokenToCode = new()
    {
        ["skc"] = 0, ["few"] = 1, ["sct"] = 2, ["bkn"] = 3, ["ovc"] = 3,
        ["wind_skc"] = 0, ["wind_few"] = 1, ["wind_sct"] = 2, ["wind_bkn"] = 3, ["wind_ovc"] = 3,
        ["fog"] = 45, ["dust"] = 45, ["haze"] = 45, ["smoke"] = 45,
        ["rain"] = 63, ["rain_showers"] = 80, ["rain_showers_hi"] = 80,
        ["snow"] = 73, ["rain_snow"] = 71, ["snow_fzra"] = 71,
        ["sleet"] = 65, ["fzra"] = 65, ["rain_fzra"] = 65,
        ["tsra"] = 95, ["tsra_sct"] = 95, ["tsra_hi"] = 95,
        ["hot"] = 0, ["cold"] = 0, ["blizzard"] = 75
    };

    /// <summary>"10 to 15 km/h" / "15 km/h" — the last number is the upper bound.</summary>
    [GeneratedRegex(@"(\d+)(?!.*\d)")]
    private static partial Regex WindSpeedPattern();

    /// <summary>NWS icon URLs carry the condition as a token: .../icons/land/day/tsra,40?size=medium</summary>
    [GeneratedRegex(@"/icons/land/(?:day|night)/([a-z_]+)")]
    private static partial Regex IconTokenPattern();

    private readonly TimeProvider _time;

    public WeatherGovConverter() : this(TimeProvider.System)
    {
    }

    /// <summary>Test seam: pins the clock so <c>fetchedUtc</c> is deterministic.</summary>
    public WeatherGovConverter(TimeProvider time)
    {
        _time = time;
    }

    public string Provider => "weather-gov";

    public int ProviderType => WeatherSourceType.WeatherGov;

    public CanonicalForecast Convert(string rawJson, string mli)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            throw new ForecastConversionException("Weather.gov payload is not valid JSON", ex);
        }

        if (root?["properties"]?["periods"] is not JsonArray periods || periods.Count == 0)
        {
            throw new ForecastConversionException(
                "Weather.gov payload has no properties.periods[]; this is not a gridpoint forecast");
        }

        var byDay = new Dictionary<DateOnly, DayParts>();
        var order = new List<DateOnly>();
        foreach (JsonNode? period in periods)
        {
            if (period is null)
            {
                continue;
            }
            string? start = (period["startTime"] as JsonValue)?.TryGetValue(out string? s) == true ? s : null;
            if (start is null || !DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTimeOffset at))
            {
                continue;
            }

            DateOnly date = DateOnly.FromDateTime(at.DateTime);
            if (!byDay.TryGetValue(date, out DayParts? parts))
            {
                parts = new DayParts();
                byDay[date] = parts;
                order.Add(date);
            }
            parts.Accept(period);
        }

        var output = new List<ForecastDay>();
        foreach (DateOnly date in order)
        {
            DayParts parts = byDay[date];
            JsonNode? lead = parts.Lead();
            if (lead is null)
            {
                continue;
            }

            int? code = CodeOf(Text(lead, "icon"));

            output.Add(new ForecastDay
            {
                Date = date,
                Time = ForecastDay.DailySummaryTime,
                TempHighC = parts.High(),
                TempLowC = parts.Low(),
                TempC = parts.RepresentativeTemperature(),
                TempDayC = parts.DaytimeTemperature(),
                HumidityPct = MeasureOf(lead, "relativeHumidity"),
                WindSpeedKmh = WindSpeedOf(Text(lead, "windSpeed")),
                WindDegrees = null,                                  // NWS gives a cardinal, never a bearing
                WindDirection = Text(lead, "windDirection"),
                PressureHpa = null,                                  // no pressure in /forecast
                PrecipChancePct = IntMeasureOf(lead, "probabilityOfPrecipitation"),
                PrecipMm = null,                                     // amount is not published by /forecast
                PrecipDayMm = null,
                PrecipNightMm = null,
                WeatherCode = code,
                Icon = "om_" + (code?.ToString(CultureInfo.InvariantCulture) ?? "na") + ".png",
                ConditionsShort = Text(lead, "shortForecast"),
                ConditionsLong = Text(lead, "detailedForecast")
            });
        }

        if (output.Count == 0)
        {
            throw new ForecastConversionException("Weather.gov payload produced no usable forecast day");
        }

        return new CanonicalForecast
        {
            Provider = Provider,
            ProviderType = ProviderType,
            Mli = mli,
            FetchedUtc = _time.GetUtcNow(),
            Days = output,
            Raw = root
        };
    }

    /// <summary>The up-to-two periods that make one calendar day.</summary>
    private sealed class DayParts
    {
        private JsonNode? _day;
        private JsonNode? _night;

        public void Accept(JsonNode period)
        {
            bool isDaytime = (period["isDaytime"] as JsonValue)?.TryGetValue(out bool d) == true && d;
            if (isDaytime)
            {
                _day ??= period;
            }
            else
            {
                _night ??= period;
            }
        }

        /// <summary>Daytime wins for the day's text and wind; a night-only day (the "Tonight" lead) uses that.</summary>
        public JsonNode? Lead() => _day ?? _night;

        public double? High() => Temperature(_day);

        public double? Low() => Temperature(_night);

        public double? DaytimeTemperature() => Temperature(_day);

        public int? RepresentativeTemperature()
        {
            double? value = Temperature(_day) ?? Temperature(_night);
            return value is null ? null : (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);
        }

        private static double? Temperature(JsonNode? period)
        {
            if (period?["temperature"] is not JsonValue value)
            {
                return null;
            }
            return value.TryGetValue(out double parsed) ? parsed : null;
        }
    }

    private static double? MeasureOf(JsonNode node, string field)
    {
        if (node[field]?["value"] is not JsonValue value)
        {
            return null;
        }
        return value.TryGetValue(out double parsed) ? parsed : null;
    }

    private static int? IntMeasureOf(JsonNode node, string field)
    {
        double? value = MeasureOf(node, field);
        return value is null ? null : (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);
    }

    private static string? Text(JsonNode node, string field)
    {
        if (node[field] is not JsonValue value || !value.TryGetValue(out string? parsed))
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(parsed) ? null : parsed.Trim();
    }

    internal static double? WindSpeedOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        Match match = WindSpeedPattern().Match(text);
        return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    internal static int? CodeOf(string? iconUrl)
    {
        if (iconUrl is null)
        {
            return null;
        }
        Match match = IconTokenPattern().Match(iconUrl);
        return match.Success && TokenToCode.TryGetValue(match.Groups[1].Value, out int code) ? code : null;
    }
}
