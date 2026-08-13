using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeatherService.Canonical;

/// <summary>
/// Converts an Open-Meteo document (<c>$.hourly</c> + <c>$.daily</c>) into the canonical envelope.
///
/// <para>Open-Meteo is already metric — °C, km/h, mm, hPa — so nothing is converted here. What this
/// does is the <em>reduction</em> that used to happen in T-SQL: the hourly arrays are parallel lists
/// indexed by position, and the database picked the latest hour of each day, summed rainfall into a
/// 06:00–17:59 daytime bucket and everything else into a night bucket, and averaged the daytime
/// temperature. That arithmetic is reproduced exactly, so a station keeps reporting the same numbers
/// across the rollout.</para>
///
/// <para>The daily arrays carry only <c>temperature_2m_max</c> / <c>temperature_2m_min</c>; every other
/// value comes from the chosen hour, which is why a day with no hourly rows is skipped rather than
/// emitted half-empty.</para>
///
/// <para>Mirrors <c>com.fishfind.weather.canonical.OpenMeteoConverter</c>.</para>
/// </summary>
public class OpenMeteoConverter : IForecastConverter
{
    private const int DayStartHour = 6;
    private const int DayEndHour = 17;

    /// <summary>WMO code → the text the legacy branch stored, kept identical so nothing downstream shifts.</summary>
    private static readonly Dictionary<int, (string Short, string Long)> CodeText = new()
    {
        [0] = ("Clear", "Clear sky"),
        [1] = ("Mainly clear", "Mainly clear sky"),
        [2] = ("Partly cloudy", "Partly cloudy"),
        [3] = ("Overcast", "Overcast"),
        [45] = ("Fog", "Fog"),
        [48] = ("Rime fog", "Depositing rime fog"),
        [51] = ("Light drizzle", "Light drizzle"),
        [53] = ("Drizzle", "Moderate drizzle"),
        [55] = ("Dense drizzle", "Dense drizzle"),
        [61] = ("Light rain", "Slight rain"),
        [63] = ("Rain", "Moderate rain"),
        [65] = ("Heavy rain", "Heavy rain"),
        [71] = ("Light snow", "Slight snow fall"),
        [73] = ("Snow", "Moderate snow fall"),
        [75] = ("Heavy snow", "Heavy snow fall"),
        [80] = ("Rain showers", "Slight rain showers"),
        [81] = ("Rain showers", "Moderate rain showers"),
        [82] = ("Heavy showers", "Violent rain showers"),
        [95] = ("Thunderstorm", "Thunderstorm")
    };

    private readonly TimeProvider _time;

    public OpenMeteoConverter() : this(TimeProvider.System)
    {
    }

    /// <summary>Test seam: pins the clock so <c>fetchedUtc</c> is deterministic.</summary>
    public OpenMeteoConverter(TimeProvider time)
    {
        _time = time;
    }

    public string Provider => "open-meteo";

    public int ProviderType => WeatherSourceType.OpenMeteo;

    public CanonicalForecast Convert(string rawJson, string mli)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            throw new ForecastConversionException("Open-Meteo payload is not valid JSON", ex);
        }

        JsonNode? hourly = root?["hourly"];
        if (hourly?["time"] is not JsonArray times || times.Count == 0)
        {
            throw new ForecastConversionException("Open-Meteo payload has no hourly.time[]");
        }

        Dictionary<DateOnly, double?> maxByDay = DailyValues(root, "temperature_2m_max");
        Dictionary<DateOnly, double?> minByDay = DailyValues(root, "temperature_2m_min");

        // one bucket per day, in the order the hours appear
        var buckets = new Dictionary<DateOnly, DayBucket>();
        var order = new List<DateOnly>();
        for (int i = 0; i < times.Count; i++)
        {
            string? stamp = (times[i] as JsonValue)?.TryGetValue(out string? s) == true ? s : null;
            if (stamp is null || !DateTime.TryParse(stamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime at))
            {
                continue;
            }

            DateOnly date = DateOnly.FromDateTime(at);
            if (!buckets.TryGetValue(date, out DayBucket? bucket))
            {
                bucket = new DayBucket();
                buckets[date] = bucket;
                order.Add(date);
            }
            bucket.Accept(at, i, hourly);
        }

        var output = new List<ForecastDay>();
        foreach (DateOnly date in order)
        {
            DayBucket bucket = buckets[date];
            if (bucket.LatestIndex < 0)
            {
                continue;
            }

            int? code = IntAt(hourly, "weather_code", bucket.LatestIndex);
            (string Short, string Long) words = code is not null && CodeText.TryGetValue(code.Value, out var found)
                ? found
                : ("Unknown", "Unknown weather condition");
            double? windDeg = DoubleAt(hourly, "wind_direction_10m", bucket.LatestIndex);
            double? airTemp = DoubleAt(hourly, "temperature_2m", bucket.LatestIndex);

            output.Add(new ForecastDay
            {
                Date = date,
                Time = bucket.LatestTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                TempHighC = maxByDay.GetValueOrDefault(date),
                TempLowC = minByDay.GetValueOrDefault(date),
                TempC = airTemp is null ? null : (int)Math.Round(airTemp.Value, MidpointRounding.AwayFromZero),
                TempDayC = bucket.DaytimeMeanTemp(),
                HumidityPct = DoubleAt(hourly, "relative_humidity_2m", bucket.LatestIndex),
                WindSpeedKmh = DoubleAt(hourly, "wind_speed_10m", bucket.LatestIndex),
                WindDegrees = windDeg,
                WindDirection = ForecastDay.Compass(windDeg),
                PressureHpa = IntAt(hourly, "pressure_msl", bucket.LatestIndex),
                PrecipChancePct = IntAt(hourly, "precipitation_probability", bucket.LatestIndex),
                PrecipMm = (int)Math.Round(bucket.RainDay + bucket.RainNight, MidpointRounding.AwayFromZero),
                PrecipDayMm = bucket.RainDay,
                PrecipNightMm = bucket.RainNight,
                WeatherCode = code,
                Icon = "om_" + (code?.ToString(CultureInfo.InvariantCulture) ?? "na") + ".png",
                ConditionsShort = words.Short,
                ConditionsLong = words.Long
            });
        }

        if (output.Count == 0)
        {
            throw new ForecastConversionException("Open-Meteo payload produced no usable forecast day");
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

    /// <summary>Accumulates one day's hours: which is latest, and how the rain and daytime temperature add up.</summary>
    private sealed class DayBucket
    {
        public int LatestIndex { get; private set; } = -1;
        public DateTime LatestTime { get; private set; }
        public double RainDay { get; private set; }
        public double RainNight { get; private set; }

        private double _daytimeTempSum;
        private int _daytimeTempCount;

        public void Accept(DateTime at, int index, JsonNode hourly)
        {
            if (LatestIndex < 0 || at > LatestTime)
            {
                LatestTime = at;
                LatestIndex = index;
            }

            bool daytime = at.Hour >= DayStartHour && at.Hour <= DayEndHour;
            double? rain = DoubleAt(hourly, "rain", index);
            if (rain is not null)
            {
                if (daytime)
                {
                    RainDay += rain.Value;
                }
                else
                {
                    RainNight += rain.Value;
                }
            }

            double? temp = DoubleAt(hourly, "temperature_2m", index);
            if (daytime && temp is not null)
            {
                _daytimeTempSum += temp.Value;
                _daytimeTempCount++;
            }
        }

        public double? DaytimeMeanTemp() => _daytimeTempCount == 0 ? null : _daytimeTempSum / _daytimeTempCount;
    }

    private static Dictionary<DateOnly, double?> DailyValues(JsonNode? root, string field)
    {
        var output = new Dictionary<DateOnly, double?>();
        JsonNode? daily = root?["daily"];
        if (daily?["time"] is not JsonArray times || daily[field] is not JsonArray values)
        {
            return output;
        }

        for (int i = 0; i < times.Count && i < values.Count; i++)
        {
            string? stamp = (times[i] as JsonValue)?.TryGetValue(out string? s) == true ? s : null;
            if (stamp is null || !DateOnly.TryParse(stamp, CultureInfo.InvariantCulture, out DateOnly date))
            {
                continue;
            }
            output[date] = (values[i] as JsonValue)?.TryGetValue(out double v) == true ? v : null;
        }
        return output;
    }

    private static double? DoubleAt(JsonNode hourly, string field, int index)
    {
        if (hourly[field] is not JsonArray array || index < 0 || index >= array.Count)
        {
            return null;
        }
        return (array[index] as JsonValue)?.TryGetValue(out double value) == true ? value : null;
    }

    private static int? IntAt(JsonNode hourly, string field, int index)
    {
        double? value = DoubleAt(hourly, field, index);
        return value is null ? null : (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);
    }
}
