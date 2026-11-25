using System.Globalization;
using EnvService.Data;
using EnvService.Web;

namespace EnvService.Reads;

/// <summary>
/// The station and water-body reads of <c>/api/v1/env/</c> (plan step 6a): validation first (400 before any cache or
/// database), then the station cache, then primary-key reads of envdata. Metric; pressure in mm Hg.
/// </summary>
public sealed class EnvReadService(StationCache cache, IEnvReadStore store, TimeProvider clock)
{
    /// <summary>Weather is kept 15 days back and forecast 15 days ahead (plan decision 1).</summary>
    public const int WeatherDaysBack = 15;
    public const int WeatherDaysAhead = 15;

    /// <summary>The longest <c>from</c>..<c>to</c> range of one weather call, both days included.</summary>
    public const int MaxWeatherDays = 31;

    public static readonly Units MetricUnits = new("°C", "mm", "mmHg", "m", "m³/s", "m/s", "km/h", "mg/L");

    public StationDto Station(int sid) => StationDto.Of(Find(sid));

    public async Task<StationCurrentDto> CurrentAsync(int sid, CancellationToken ct)
    {
        StationInfo station = Find(sid);
        IReadOnlyList<StationStateRow> rows = await store.CurrentAsync([station.Sid], ct);
        return new StationCurrentDto(station.Sid, station.Mli, MetricUnits, rows.Count == 0 ? null : CurrentDto.Of(rows[0]));
    }

    public async Task<StationWeatherDto> WeatherAsync(int sid, string? from, string? to, CancellationToken ct)
    {
        (DateOnly f, DateOnly t) = WeatherRange(from, to);
        StationInfo station = Find(sid);
        IReadOnlyList<WeatherDayRow> rows = await store.WeatherAsync([station.Sid], f, t, ct);
        return new StationWeatherDto(station.Sid, station.Mli, Iso(f), Iso(t), MetricUnits, rows.Select(WeatherDayDto.Of).ToList());
    }

    public LakeStationsDto LakeStations(string lakeId)
    {
        string id = LakeId(lakeId);
        IReadOnlyList<StationInfo> stations = LakeStationList(id);
        return new LakeStationsDto(id, stations.Count == 0 ? null : stations[0].LakeName,
            stations.Select(StationDto.Of).ToList());
    }

    /// <summary>
    /// The water body's stations, each with its current reading and its weather of <paramref name="date"/> (default
    /// today, UTC): the website's Weather tab, and the weather a catch log of that date is stamped with.
    /// </summary>
    public async Task<LakeWeatherDto> LakeWeatherAsync(string lakeId, string? date, CancellationToken ct)
    {
        string id = LakeId(lakeId);
        DateOnly today = Today();
        DateOnly day = date is null ? today : ParseDate(date, "date");
        if (day < today.AddDays(-WeatherDaysBack) || day > today.AddDays(WeatherDaysAhead))
        {
            throw new InvalidRequestException(
                $"date must be within {WeatherDaysBack} days back and {WeatherDaysAhead} days ahead of today ({Iso(today)})");
        }
        IReadOnlyList<StationInfo> stations = LakeStationList(id);
        int[] sids = stations.Select(s => s.Sid).ToArray();
        Task<IReadOnlyList<StationStateRow>> current = store.CurrentAsync(sids, ct);
        Task<IReadOnlyList<WeatherDayRow>> weather = store.WeatherAsync(sids, day, day, ct);
        Dictionary<int, StationStateRow> bySid = (await current).ToDictionary(r => r.Sid);
        Dictionary<int, WeatherDayRow> weatherBySid = (await weather).ToDictionary(r => r.Sid);
        return new LakeWeatherDto(id, stations.Count == 0 ? null : stations[0].LakeName, Iso(day), MetricUnits,
            stations.Select(s => new LakeStationWeatherDto(
                StationDto.Of(s),
                bySid.TryGetValue(s.Sid, out StationStateRow? c) ? CurrentDto.Of(c) : null,
                weatherBySid.TryGetValue(s.Sid, out WeatherDayRow? w) ? WeatherDayDto.Of(w) : null)).ToList());
    }

    /// <summary>How long a browser may reuse a map answer before revalidating with its ETag (waterapi's value).</summary>
    public const int MapClientMaxAgeSeconds = 300;

    /// <summary>
    /// Every map pin of a country, optionally one province/state: waterapi's <c>/api/v1/water/station/map</c> body,
    /// answered from the station cache (no database read). The ETag changes with every cache load and names the view.
    /// </summary>
    public StationMapResult StationMap(string? country, string? state)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            throw new InvalidRequestException("country is required (" + string.Join(" or ", StationCache.Snapshot.MapCountries) + ")");
        }
        string c = country.Trim().ToUpperInvariant();
        if (!StationCache.Snapshot.MapCountries.Contains(c, StringComparer.Ordinal))
        {
            throw new InvalidRequestException(
                $"country must be one of {string.Join(", ", StationCache.Snapshot.MapCountries)}; got '{country.Trim()}'");
        }
        string? st = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            st = state.Trim().ToUpperInvariant();
            if (st.Length != 2 || !char.IsAsciiLetterUpper(st[0]) || !char.IsAsciiLetterUpper(st[1]))
            {
                throw new InvalidRequestException($"state must be a two-letter code; got '{state.Trim()}'");
            }
        }
        StationCache.Snapshot snap = Snapshot();
        IReadOnlyList<StationInfo> all = snap.MapByCountry.TryGetValue(c, out IReadOnlyList<StationInfo>? list) ? list : [];
        IReadOnlyList<StationInfo> view = st is null
            ? all
            : all.Where(s => string.Equals(s.State?.Trim(), st, StringComparison.OrdinalIgnoreCase)).ToList();
        string etag = $"\"{c}-{st ?? "ALL"}-{snap.LoadedUtc.Ticks:x}-{all.Count}\"";
        return new StationMapResult(etag,
            new StationMapDto(c, st, view.Count, snap.LoadedUtc, view.Select(StationPinDto.Of).ToList()));
    }

    private StationCache.Snapshot Snapshot() =>
        cache.Current ?? throw new ServiceUnavailableException("The station list is not loaded yet; try again shortly");

    private StationInfo Find(int sid)
    {
        if (sid <= 0)
        {
            throw new InvalidRequestException("sid must be a positive integer");
        }
        return Snapshot().BySid.TryGetValue(sid, out StationInfo? s) ? s : throw new NotFoundException($"Unknown station {sid}");
    }

    private IReadOnlyList<StationInfo> LakeStationList(string lakeId) =>
        Snapshot().ByLake.TryGetValue(lakeId, out IReadOnlyList<StationInfo>? list) ? list : [];

    private (DateOnly From, DateOnly To) WeatherRange(string? from, string? to)
    {
        DateOnly today = Today();
        DateOnly f = from is null ? today.AddDays(-WeatherDaysBack) : ParseDate(from, "from");
        DateOnly t = to is null ? today.AddDays(WeatherDaysAhead) : ParseDate(to, "to");
        if (t < f)
        {
            throw new InvalidRequestException("to must not be before from");
        }
        if (t.DayNumber - f.DayNumber + 1 > MaxWeatherDays)
        {
            throw new InvalidRequestException($"from..to spans at most {MaxWeatherDays} days");
        }
        return (f, t);
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    internal static string LakeId(string? lakeId) =>
        Guid.TryParseExact(lakeId?.Trim(), "D", out Guid g)
            ? g.ToString("D")
            : throw new InvalidRequestException("lakeId must be a GUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)");

    private static DateOnly ParseDate(string value, string name) =>
        DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d)
            ? d
            : throw new InvalidRequestException($"{name} must be a date yyyy-MM-dd");

    internal static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>The units every <c>/api/v1/env/</c> answer uses.</summary>
public sealed record Units(
    string Temperature, string Precipitation, string Pressure, string Level, string Discharge, string Velocity,
    string WindSpeed, string Oxygen);

/// <summary>A station: identity, place and water-body link. <c>lastReading</c>/<c>lastWeather</c> are UTC.</summary>
public sealed record StationDto(
    int Sid, string Mli, string Country, string? State, double Lat, double Lon, int? Tz, int LocType, string LocName,
    string? LakeId, string? LakeName, int? Elevation, string? Agency, bool HasFish, bool Supported,
    DateTime? LastReading, DateTime? LastWeather)
{
    public static StationDto Of(StationInfo s) => new(
        s.Sid, s.Mli, s.Country, s.State, s.Lat, s.Lon, s.Tz, s.LocType, s.LocName, s.LakeId?.ToLowerInvariant(),
        s.LakeId is null ? null : s.LakeName, s.Elevation, s.Agency, s.HasFish, s.Supported, s.UpdData, s.WeatherStamp);
}

/// <summary>A map answer and its entity tag.</summary>
public sealed record StationMapResult(string ETag, StationMapDto Body);

/// <summary><c>data</c> of <c>/stations/map</c>: waterapi's <c>/water/station/map</c> body, field for field.</summary>
public sealed record StationMapDto(
    string Country, string? State, int Count, DateTime LoadedUtc, IReadOnlyList<StationPinDto> Stations);

/// <summary>One map pin. <c>key</c> = the sid in base 36 (the map's links); <c>stamp</c> = the last reading, UTC.</summary>
public sealed record StationPinDto(int Sid, string Key, double Lat, double Lon, string State, DateTime Stamp)
{
    private const string Base36Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static StationPinDto Of(StationInfo s) =>
        new(s.Sid, ToBase36((ulong)s.Sid), s.Lat, s.Lon, s.State?.Trim() ?? "", s.UpdData!.Value);

    /// <summary>Upper-case base 36, identical to waterapi's and the legacy map page's encoder.</summary>
    public static string ToBase36(ulong value)
    {
        Span<char> buffer = stackalloc char[13];
        int pos = buffer.Length;
        do
        {
            buffer[--pos] = Base36Digits[(int)(value % 36)];
            value /= 36;
        } while (value != 0);
        return new string(buffer[pos..]);
    }
}

/// <summary>The flow state of a station (envdata <c>sp_fill_flow_state</c>); null fields = not computed.</summary>
public sealed record FlowDto(
    string? State, double? QRatio1d, double? QRatio2d, double? QRatio3To7d, double? QMedian30d, int? DaysSincePeak,
    DateTime? Stamp);

/// <summary>The newest values a station reported (each the last non-empty one) and its flow state.</summary>
public sealed record CurrentDto(
    DateTime Ts, double? Discharge, double? Turbidity, double? Oxygen, decimal? Ph, double? Level, FlowDto Flow)
{
    public static CurrentDto Of(StationStateRow r) => new(
        DateTime.SpecifyKind(r.Ts, DateTimeKind.Utc), r.Discharge, r.Turbidity, r.Oxygen, r.Ph, r.Elevation,
        new FlowDto(r.FlowState, r.QRatio1d, r.QRatio2d, r.QRatio37d, r.QMedian30d, r.DaysSincePeak,
            r.FlowStamp is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null));
}

/// <summary>The pressure picture of one day (envdata <c>sp_fill_pressure_state</c>), mm Hg.</summary>
public sealed record PressureDayDto(string? State, decimal? ChangeMm, string? FrontDate, decimal? SwingMm, decimal? Strength);

/// <summary>
/// One day of a station's weather. <c>precipDayMm</c>/<c>precipNightMm</c> = the provider's day and night amounts,
/// <c>rainTodayMm</c> = the day's total; <c>provider</c> = envdata provider type (1 TWC, 2 Open-Meteo, 4 Visual Crossing,
/// 5 weather.gov, 6 EC, 7 WU, 8 Google). <c>pressure</c> is null when the day has no computed pressure state.
/// </summary>
public sealed record WeatherDayDto(
    string Date, string? Time, double? TempHighC, double? TempLowC, double? TempDayC, int? AirTempC, double PrecipDayMm,
    double PrecipNightMm, int? RainTodayMm, int? PrecipChancePct, double? HumidityPct, double? WindMaxKmh,
    double? WindDegree, string? WindDirection, decimal? PressureMm, int? WeatherCode, string? Icon, string? Conditions,
    string? ConditionsLong, int Provider, DateTime Stamp, PressureDayDto? Pressure)
{
    public static WeatherDayDto Of(WeatherDayRow w) => new(
        EnvReadService.Iso(w.Dt), w.Tm?.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture), w.TmHigh, w.TmLow, w.TmDay,
        w.AirTemperature, w.GpfDay, w.GpfNight, w.RainToday, w.Pop, w.Humidity, w.WindMaxSpeed, w.WindDegree,
        w.WindDirection, w.PressureMm, w.WeatherCode, w.Icon, w.ShortText, w.LongText, w.ProviderType,
        DateTime.SpecifyKind(w.Stamp, DateTimeKind.Utc),
        w.PressureState is null && w.SwingMm is null && w.FrontDt is null
            ? null
            : new PressureDayDto(w.PressureState, w.D1Mm, w.FrontDt is { } f ? EnvReadService.Iso(f) : null, w.SwingMm, w.Strength));
}

public sealed record StationCurrentDto(int Sid, string Mli, Units Units, CurrentDto? Current);

public sealed record StationWeatherDto(int Sid, string Mli, string From, string To, Units Units, IReadOnlyList<WeatherDayDto> Days);

public sealed record LakeStationsDto(string LakeId, string? LakeName, IReadOnlyList<StationDto> Stations);

public sealed record LakeStationWeatherDto(StationDto Station, CurrentDto? Current, WeatherDayDto? Weather);

public sealed record LakeWeatherDto(
    string LakeId, string? LakeName, string Date, Units Units, IReadOnlyList<LakeStationWeatherDto> Stations);
