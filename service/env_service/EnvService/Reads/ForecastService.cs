using System.Globalization;
using System.Text.Json.Nodes;
using EnvService.Data;
using EnvService.Web;
using Microsoft.Extensions.Caching.Memory;

namespace EnvService.Reads;

/// <summary>
/// The three forecast documents of <c>/api/v1/env/forecast/</c> (plan step 6b), the bodies docapi's
/// <c>/api/v1/forecast/*</c> returns from step 6c on. The fish half comes from SQL Server (<see cref="IForecastStore"/>,
/// schema env), the water and weather half from MySQL envdata (<see cref="IEnvReadStore"/>). Same parameters and rules
/// as docapi's ForecastController; pressure in mm Hg. Answers are kept 5 minutes (the score changes at most with each
/// hand-over), at most <see cref="CacheEntries"/> of them.
/// </summary>
public sealed class ForecastService : IDisposable
{
    public const int DefaultRadiusKm = 100, MaxRadiusKm = 500, DefaultLimit = 50, MaxLimit = 200, MaxDay = 6;
    public const int PlotDaysBack = 15, PlotDaysAhead = 7, CalendarRows = 23;
    private const int CacheEntries = 2000;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly IForecastStore _fish;
    private readonly IEnvReadStore _env;
    private readonly StationCache _stations;
    private readonly TimeProvider _clock;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = CacheEntries });

    public ForecastService(IForecastStore fish, IEnvReadStore env, StationCache stations, TimeProvider clock)
    {
        _fish = fish;
        _env = env;
        _stations = stations;
        _clock = clock;
    }

    /// <summary>The forecast-map stations near a point for one fish, nearest first.</summary>
    public Task<JsonNode> StationsAsync(string? fish, string? lat, string? lon, string? radius, string? country, string? limit,
        CancellationToken ct)
    {
        Guid fishId = RequireFish(fish);
        double la = Round2(RequireCoordinate(lat, "lat", 90));
        double lo = Round2(RequireCoordinate(lon, "lon", 180));
        int r = Clamp(radius, DefaultRadiusKm, MaxRadiusKm);
        int n = Clamp(limit, DefaultLimit, MaxLimit);
        string? c = ParseCountry(country);
        string key = FormattableString.Invariant($"s|{fishId}|{la}|{lo}|{r}|{c}|{n}");
        return Cached(key, async () =>
        {
            string? json = await _fish.StationsJsonAsync(fishId, la, lo, r, c, n, ct);
            return json is null ? throw new NotFoundException($"Unknown fish {fishId}") : JsonNode.Parse(json)!;
        });
    }

    /// <summary>Reasoning.aspx's document for <paramref name="day"/> 0..6 days from today (UTC).</summary>
    public Task<JsonNode> ReasoningAsync(string sid, string? fish, string? day, CancellationToken ct)
    {
        int s = RequireSid(sid);
        Guid fishId = RequireFish(fish);
        int d = ParseDay(day);
        DateTime at = _clock.GetUtcNow().UtcDateTime.AddDays(d);
        string key = FormattableString.Invariant($"r|{s}|{fishId}|{DateOnly.FromDateTime(at)}");
        return Cached(key, async () => await ReasoningDocAsync(s, fishId, at, ct)
            ?? throw new NotFoundException($"Unknown station {s} / fish {fishId}"));
    }

    /// <summary>Plot.aspx in one document: <c>{sid, fishId, plot, calendar, week}</c>.</summary>
    public Task<JsonNode> PlotAsync(string sid, string? fish, CancellationToken ct)
    {
        int s = RequireSid(sid);
        Guid fishId = RequireFish(fish);
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        DateOnly today = DateOnly.FromDateTime(now);
        string key = FormattableString.Invariant($"p|{s}|{fishId}|{today}");
        return Cached(key, async () =>
        {
            string? metaJson = await _fish.PlotMetaJsonAsync(s, fishId, ct);
            if (metaJson is null)
            {
                throw new NotFoundException($"Unknown station {s} / fish {fishId}");
            }
            Task<JsonNode?> week = ReasoningDocAsync(s, fishId, now, ct);
            Task<IReadOnlyList<WaterDayRow>> water = _env.WaterDaysAsync(s, today.AddDays(-PlotDaysBack), today.AddDays(PlotDaysAhead), ct);
            Task<IReadOnlyList<WeatherDayRow>> weather =
                _env.WeatherAsync([s], today.AddDays(-PlotDaysBack), today.AddDays(PlotDaysBack), ct);
            JsonObject meta = JsonNode.Parse(metaJson)!.AsObject();
            StationInfo? info = _stations.Current?.BySid.GetValueOrDefault(s);
            IReadOnlyList<WeatherDayRow> days = await weather;
            return new JsonObject
            {
                ["sid"] = s,
                ["fishId"] = fishId.ToString("D"),
                ["plot"] = PlotDocument(meta, info, await water, days, today),
                ["calendar"] = Calendar(days, today),
                ["week"] = (await week)?["week"]?.DeepClone() ?? new JsonArray(),
            };
        });
    }

    /// <summary>env.fn_fish_reasoning_json with its weather blocks filled from envdata (day 0 and each day of the week).</summary>
    private async Task<JsonNode?> ReasoningDocAsync(int sid, Guid fishId, DateTime at, CancellationToken ct)
    {
        string? json = await _fish.ReasoningJsonAsync(sid, fishId, at, ct);
        if (json is null)
        {
            return null;
        }
        JsonObject doc = JsonNode.Parse(json)!.AsObject();
        DateOnly day0 = DateOnly.FromDateTime(at);
        Dictionary<DateOnly, WeatherDayRow> weather = (await _env.WeatherAsync([sid], day0, day0.AddDays(MaxDay), ct))
            .ToDictionary(w => w.Dt);
        if (weather.TryGetValue(day0, out WeatherDayRow? w0))
        {
            doc["weather"] = Compact(new JsonObject
            {
                ["conditions"] = w0.ShortText,
                ["airHigh"] = R(w0.TmHigh, 1),
                ["airLow"] = R(w0.TmLow, 1),
                ["precipChance"] = w0.Pop,
                ["precipMm"] = w0.RainToday,
                ["wind"] = R(w0.WindMaxSpeed, 1),
                ["windDirection"] = w0.WindDirection,
            });
        }
        if (doc["week"] is JsonArray week)
        {
            foreach (JsonObject item in week.OfType<JsonObject>())
            {
                if (DateOnly.TryParseExact((string?)item["date"], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateOnly dt) && weather.TryGetValue(dt, out WeatherDayRow? w))
                {
                    item["weather"] = Compact(new JsonObject
                    {
                        ["conditions"] = w.ShortText,
                        ["airHigh"] = R(w.TmHigh, 1),
                        ["airLow"] = R(w.TmLow, 1),
                        ["precipChance"] = w.Pop,
                    });
                }
            }
        }
        return doc;
    }

    /// <summary>
    /// dbo.fn_forecast_plot_json's document: 23 days (today − 15 .. today + 7), each series one value per day or null.
    /// Water = the day's means (envdata v_water_day); temperature = today's air temperature, other days the day
    /// temperature; precipitation = (day + night) / 2; pressure in mm Hg.
    /// </summary>
    internal static JsonObject PlotDocument(
        JsonObject meta, StationInfo? info, IReadOnlyList<WaterDayRow> water, IReadOnlyList<WeatherDayRow> weather, DateOnly today)
    {
        Dictionary<DateOnly, WaterDayRow> waterByDay = water.ToDictionary(w => w.Dt);
        Dictionary<DateOnly, WeatherDayRow> weatherByDay = weather.ToDictionary(w => w.Dt);
        var dates = new JsonArray();
        var discharge = new JsonArray();
        var precipitation = new JsonArray();
        var temperature = new JsonArray();
        var turbidity = new JsonArray();
        var level = new JsonArray();
        var pressure = new JsonArray();
        for (DateOnly d = today.AddDays(-PlotDaysBack); d <= today.AddDays(PlotDaysAhead); d = d.AddDays(1))
        {
            dates.Add(d.ToString("ddd", CultureInfo.InvariantCulture) + " " + d.Day.ToString(CultureInfo.InvariantCulture));
            WaterDayRow? wa = waterByDay.GetValueOrDefault(d);
            WeatherDayRow? we = weatherByDay.GetValueOrDefault(d);
            discharge.Add(R(wa?.Discharge, 3));
            turbidity.Add(R(wa?.Turbidity, 2));
            level.Add(R(wa?.Level, 3));
            temperature.Add(we is null ? null : R(d == today ? we.AirTemperature : we.TmDay, 1));
            precipitation.Add(we is null ? null : R((we.GpfDay + we.GpfNight) / 2.0, 2));
            pressure.Add(we?.PressureMm is { } mm ? JsonValue.Create(mm) : null);
        }
        return new JsonObject
        {
            ["place"] = (string?)meta["place"] ?? "unknown",
            ["fish"] = (string?)meta["fish"] ?? "",
            ["country"] = (string?)meta["country"] ?? "",
            ["state"] = (string?)meta["state"] ?? "",
            ["stamp"] = info?.UpdData is { } u ? u.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) : "",
            ["lakeid"] = (string?)meta["lakeid"] ?? "",
            ["lakename"] = (string?)meta["lakename"] ?? "",
            ["units"] = new JsonObject
            {
                ["temperature"] = "°C", ["precipitation"] = "mm", ["level"] = "m", ["discharge"] = "m³/s", ["pressure"] = "mmHg",
            },
            ["date"] = dates,
            ["discharge"] = discharge,
            ["precipitation"] = precipitation,
            ["temperature"] = temperature,
            ["turbidity"] = turbidity,
            ["level"] = level,
            ["pressureMm"] = pressure,
        };
    }

    /// <summary>
    /// dbo.fn_plot_weather's first 23 rows: every stored weather day from today − 15 on plus a placeholder (zeros, empty
    /// text) for each missing day of today − 14 .. today + 15, by date. Integer columns are truncated as SQL's CAST does.
    /// </summary>
    internal static JsonArray Calendar(IReadOnlyList<WeatherDayRow> weather, DateOnly today)
    {
        Dictionary<DateOnly, WeatherDayRow> byDay = weather.Where(w => w.Dt >= today.AddDays(-PlotDaysBack)).ToDictionary(w => w.Dt);
        var dates = new SortedSet<DateOnly>(byDay.Keys);
        for (int i = 1; i <= 30; i++)
        {
            dates.Add(today.AddDays(i - PlotDaysBack));
        }
        var rows = new JsonArray();
        foreach (DateOnly d in dates.Take(CalendarRows))
        {
            WeatherDayRow? w = byDay.GetValueOrDefault(d);
            rows.Add(new JsonObject
            {
                ["dt"] = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["wind_degree"] = w is null ? 0 : w.WindDegree,
                ["precipitation"] = w?.RainToday ?? 0,
                ["wind_direction"] = w is null ? "" : w.WindDirection,
                ["temperature_low"] = w is null ? 0 : Truncate(w.TmLow),
                ["temperature_high"] = w is null ? 0 : Truncate(w.TmHigh),
                ["wind_max_speed"] = w is null ? 0 : Truncate(w.WindMaxSpeed),
                ["icon"] = w?.Icon,
                ["shortText"] = w is null ? "" : w.ShortText,
            });
        }
        return rows;
    }

    private async Task<JsonNode> Cached(string key, Func<Task<JsonNode>> load)
    {
        if (_cache.TryGetValue(key, out JsonNode? hit) && hit is not null)
        {
            return hit.DeepClone();
        }
        JsonNode value = await load();
        _cache.Set(key, value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = CacheFor });
        return value.DeepClone();
    }

    private static JsonObject Compact(JsonObject o)
    {
        foreach (string k in o.Where(p => p.Value is null).Select(p => p.Key).ToList())
        {
            o.Remove(k);
        }
        return o;
    }

    private static double? R(double? v, int digits) => v is { } x ? Math.Round(x, digits) : null;

    /// <summary>SQL's CAST(ROUND(x, 1) AS INT): round to one decimal, then truncate toward zero.</summary>
    private static int? Truncate(double? v) => v is { } x ? (int)Math.Truncate(Math.Round(x, 1)) : null;

    internal static Guid RequireFish(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidRequestException("fish is required (a fish GUID)");
        }
        return Guid.TryParse(value.Trim(), out Guid g) ? g : throw new InvalidRequestException("fish is not a valid GUID");
    }

    internal static int RequireSid(string value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s > 0
            ? s
            : throw new InvalidRequestException("sid must be a positive integer station id");

    private static double RequireCoordinate(string? value, string name, double max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidRequestException(name + " is required");
        }
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
               && double.IsFinite(v) && v >= -max && v <= max
            ? v
            : throw new InvalidRequestException(FormattableString.Invariant($"{name} must be a number between -{max} and {max}"));
    }

    internal static double Round2(double v) => Math.Round(v * 100.0, MidpointRounding.AwayFromZero) / 100.0;

    /// <summary>docapi's clamp: absent or unreadable = the default, below 1 = the default, above max = max.</summary>
    internal static int Clamp(string? value, int dflt, int max)
    {
        if (value is null || !double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            return dflt;
        }
        int n = (int)Math.Round(d, MidpointRounding.AwayFromZero);
        return n < 1 ? dflt : Math.Min(n, max);
    }

    private static string? ParseCountry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string c = value.Trim().ToUpperInvariant();
        return c is "CA" or "US" ? c : throw new InvalidRequestException("country must be CA or US");
    }

    private static int ParseDay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }
        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) && d is >= 0 and <= MaxDay
            ? d
            : throw new InvalidRequestException($"day must be 0..{MaxDay}");
    }

    public void Dispose() => _cache.Dispose();
}
