using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeatherService.Canonical;

/// <summary>
/// Converts a Visual Crossing timeline document (<c>$.days[]</c>) into the canonical envelope.
///
/// <para><b>Units are the trap.</b> <c>VisualCrossingFetcher</c> requests <c>unitGroup=us</c>, so the
/// document is °F, mph and inches while everything downstream expects metric. The request deliberately
/// stays in US units during the rollout: the legacy T-SQL branch converts the same way, so the embedded
/// <c>raw</c> document remains a valid fallback for exactly these bytes. Switching the fetcher to
/// <c>unitGroup=metric</c> would double-convert anything still taking the legacy path.</para>
///
/// <para>Two behaviours are carried over deliberately from that branch so the two paths agree during
/// the rollout: the horizon is clipped to today..today+6 (the document runs 15 days and its first day
/// is often <em>yesterday</em> in the station's time zone), and the day's rainfall is split evenly
/// between day and night because a daily document has no hourly resolution — the sum is what consumers
/// use.</para>
///
/// <para>Mirrors <c>com.fishfind.weather.canonical.VisualCrossingConverter</c>.</para>
/// </summary>
public class VisualCrossingConverter : IForecastConverter
{
    /// <summary>Provider icon → the WMO-style codes the Open-Meteo path already stores, keeping one icon namespace.</summary>
    private static readonly Dictionary<string, int> IconToCode = new()
    {
        ["clear-day"] = 0,
        ["clear-night"] = 0,
        ["wind"] = 1,
        ["partly-cloudy-day"] = 2,
        ["partly-cloudy-night"] = 2,
        ["cloudy"] = 3,
        ["fog"] = 45,
        ["rain"] = 63,
        ["showers-day"] = 80,
        ["showers-night"] = 80,
        ["snow"] = 73,
        ["snow-showers-day"] = 71,
        ["snow-showers-night"] = 71,
        ["sleet"] = 65,
        ["hail"] = 95,
        ["thunder-rain"] = 95,
        ["thunder-showers-day"] = 95,
        ["thunder-showers-night"] = 95
    };

    /// <summary>Same horizon the Open-Meteo document produces.</summary>
    internal const int HorizonDays = 7;

    private readonly TimeProvider _time;

    public VisualCrossingConverter() : this(TimeProvider.System)
    {
    }

    /// <summary>Test seam: pins the clock so the horizon and <c>fetchedUtc</c> are deterministic.</summary>
    public VisualCrossingConverter(TimeProvider time)
    {
        _time = time;
    }

    public string Provider => "visual-crossing";

    public int ProviderType => WeatherSourceType.VisualCrossing;

    public CanonicalForecast Convert(string rawJson, string mli)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            throw new ForecastConversionException("Visual Crossing payload is not valid JSON", ex);
        }

        if (root?["days"] is not JsonArray days || days.Count == 0)
        {
            throw new ForecastConversionException(
                "Visual Crossing payload has no days[]; got members " + FieldNames(root));
        }

        DateTimeOffset now = _time.GetUtcNow();
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        DateOnly last = today.AddDays(HorizonDays - 1);

        var output = new List<ForecastDay>();
        foreach (JsonNode? dayNode in days)
        {
            if (dayNode is null)
            {
                continue;
            }

            string? text = dayNode["datetime"]?.GetValue<string>();
            if (text is null || !DateOnly.TryParse(text, CultureInfo.InvariantCulture, out DateOnly date))
            {
                continue;
            }
            if (date < today || date > last)
            {
                continue;
            }

            double? tempMaxC = FahrenheitToCelsius(Number(dayNode, "tempmax"));
            double? tempMinC = FahrenheitToCelsius(Number(dayNode, "tempmin"));
            double? tempMeanC = FahrenheitToCelsius(Number(dayNode, "temp"));
            double? precipMm = InchesToMillimetres(Number(dayNode, "precip"));
            double? windKmh = MilesToKilometres(Number(dayNode, "windspeed"));
            double? windDeg = Number(dayNode, "winddir");
            int? code = IconToCode.TryGetValue(Text(dayNode, "icon") ?? string.Empty, out int mapped)
                ? mapped
                : null;

            output.Add(new ForecastDay
            {
                Date = date,
                Time = ForecastDay.DailySummaryTime,
                TempHighC = tempMaxC,
                TempLowC = tempMinC,
                TempC = Round(tempMeanC),
                TempDayC = tempMeanC,
                HumidityPct = Number(dayNode, "humidity"),
                WindSpeedKmh = windKmh,
                WindDegrees = windDeg,
                WindDirection = ForecastDay.Compass(windDeg),
                PressureHpa = Round(Number(dayNode, "pressure")),
                PrecipChancePct = Round(Number(dayNode, "precipprob")),
                PrecipMm = Round(precipMm),
                PrecipDayMm = Half(precipMm),
                PrecipNightMm = Half(precipMm),
                WeatherCode = code,
                Icon = "om_" + (code?.ToString(CultureInfo.InvariantCulture) ?? "na") + ".png",
                ConditionsShort = Text(dayNode, "conditions"),
                ConditionsLong = Text(dayNode, "description")
            });
        }

        if (output.Count == 0)
        {
            throw new ForecastConversionException(
                $"Visual Crossing payload has no day inside {today:yyyy-MM-dd}..{last:yyyy-MM-dd}; "
                + "the document may be entirely in the past");
        }

        return new CanonicalForecast
        {
            Provider = Provider,
            ProviderType = ProviderType,
            Mli = mli,
            FetchedUtc = now,
            Days = output,
            Raw = root
        };
    }

    private static double? FahrenheitToCelsius(double? f) => f is null ? null : (f.Value - 32.0) * 5.0 / 9.0;

    private static double? InchesToMillimetres(double? inches) => inches is null ? null : inches.Value * 25.4;

    private static double? MilesToKilometres(double? miles) => miles is null ? null : miles.Value * 1.609344;

    /// <summary>A daily document cannot say when the rain fell, so the total is split rather than claimed for daylight.</summary>
    private static double? Half(double? total) => total is null ? null : total.Value / 2.0;

    private static int? Round(double? value) => value is null ? null : (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);

    private static double? Number(JsonNode node, string field)
    {
        JsonNode? value = node[field];
        if (value is not JsonValue jsonValue)
        {
            return null;
        }
        return jsonValue.TryGetValue(out double parsed) ? parsed : null;
    }

    private static string? Text(JsonNode node, string field)
    {
        JsonNode? value = node[field];
        if (value is not JsonValue jsonValue)
        {
            return null;
        }
        return jsonValue.TryGetValue(out string? parsed) ? parsed : null;
    }

    private static string FieldNames(JsonNode? root) =>
        root is JsonObject obj ? "[" + string.Join(", ", obj.Select(p => p.Key)) + "]" : "[]";
}
