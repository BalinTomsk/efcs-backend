using System.Text.Json;
using EnvService.Data;
using EnvService.Reads;
using EnvService.Web;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;

namespace EnvService.Tests;

internal sealed class FakeReadStore : IEnvReadStore
{
    public List<StationInfo> Stations { get; } = [];
    public List<StationStateRow> Current { get; } = [];
    public List<WeatherDayRow> Weather { get; } = [];
    public int Calls;
    public (DateOnly From, DateOnly To)? LastRange;

    public Task<IReadOnlyList<StationInfo>> StationInfosAsync(CancellationToken ct) =>
        Task.FromResult((IReadOnlyList<StationInfo>)Stations.ToList());

    public Task<IReadOnlyList<StationStateRow>> CurrentAsync(IReadOnlyCollection<int> sids, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult((IReadOnlyList<StationStateRow>)Current.Where(c => sids.Contains(c.Sid)).ToList());
    }

    public Task<IReadOnlyList<WeatherDayRow>> WeatherAsync(
        IReadOnlyCollection<int> sids, DateOnly from, DateOnly to, CancellationToken ct)
    {
        Calls++;
        LastRange = (from, to);
        return Task.FromResult((IReadOnlyList<WeatherDayRow>)Weather
            .Where(w => sids.Contains(w.Sid) && w.Dt >= from && w.Dt <= to).ToList());
    }

    public List<WaterDayRow> Water { get; } = [];

    public Task<IReadOnlyList<WaterDayRow>> WaterDaysAsync(int sid, DateOnly from, DateOnly to, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult((IReadOnlyList<WaterDayRow>)Water.Where(w => w.Sid == sid && w.Dt >= from && w.Dt <= to).ToList());
    }
}

public class EnvReadServiceTests
{
    private const string Lake = "0d012b12-849c-20c3-8532-2f7a21cfcc58";
    private static readonly DateTime Now = new(2026, 11, 25, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 25);

    private static StationInfo Station(int sid, string mli, string? lake) => new(
        sid, mli, Guid.NewGuid().ToString(), "CA", "ON", 45.5, -79.25, -5, 2, "gauge " + mli,
        lake is null ? null : lake.ToUpperInvariant(), "Test River", null, null, true, true, Now.AddHours(-1), null);

    private static WeatherDayRow Day(int sid, DateOnly dt, decimal? mm, string? state = null) => new(
        sid, dt, new TimeSpan(12, 0, 0), 5, -1, 3, 2, 1.5, 0.5, 2, 40, 80, 12, 270, "W", mm, 61, "rain", "Rain", "Light rain",
        2, Now, state, state is null ? null : -3.5m, null, state is null ? null : 6.0m, state is null ? null : 0.6m);

    private static (EnvReadService Service, FakeReadStore Store, StationCache Cache) Make(bool loaded = true)
    {
        var store = new FakeReadStore();
        store.Stations.AddRange([Station(12, "B02", Lake), Station(11, "A01", Lake), Station(13, "C03", null)]);
        var cache = new StationCache();
        if (loaded)
        {
            cache.Load(store.Stations, Now);
        }
        return (new EnvReadService(cache, store, new FakeTimeProvider(Now)), store, cache);
    }

    [Test]
    public async Task Station_FromTheCache_LakeIdLowerCase()
    {
        (EnvReadService s, FakeReadStore store, _) = Make();

        StationDto dto = s.Station(12);

        await Assert.That(dto.Mli).IsEqualTo("B02");
        await Assert.That(dto.LakeId).IsEqualTo(Lake);
        await Assert.That(store.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Station_UnknownIs404_BadIs400_NotLoadedIs503()
    {
        (EnvReadService s, _, _) = Make();
        (EnvReadService cold, _, _) = Make(loaded: false);

        await Assert.That(() => s.Station(99)).Throws<NotFoundException>();
        await Assert.That(() => s.Station(0)).Throws<InvalidRequestException>();
        await Assert.That(() => cold.Station(12)).Throws<ServiceUnavailableException>();
    }

    [Test]
    public async Task Current_NeverReportedIsNull_ElseTheRow()
    {
        (EnvReadService s, FakeReadStore store, _) = Make();
        store.Current.Add(new StationStateRow(11, "A01", Now.AddHours(-2), 12.5, null, 9.1, 7.62m, 1.25, "RISING",
            1.2, 1.1, 1.0, 10, 2, Now));

        StationCurrentDto none = await s.CurrentAsync(12, default);
        StationCurrentDto one = await s.CurrentAsync(11, default);

        await Assert.That(none.Current).IsNull();
        await Assert.That(one.Current!.Discharge).IsEqualTo(12.5);
        await Assert.That(one.Current.Level).IsEqualTo(1.25);
        await Assert.That(one.Current.Flow.State).IsEqualTo("RISING");
        await Assert.That(one.Units.Pressure).IsEqualTo("mmHg");
    }

    [Test]
    public async Task Weather_DefaultsTo15Back15Ahead()
    {
        (EnvReadService s, FakeReadStore store, _) = Make();

        StationWeatherDto dto = await s.WeatherAsync(11, null, null, default);

        await Assert.That(store.LastRange).IsEqualTo((Today.AddDays(-15), Today.AddDays(15)));
        await Assert.That(dto.From).IsEqualTo("2026-11-10");
        await Assert.That(dto.To).IsEqualTo("2026-12-10");
    }

    [Test]
    public async Task Weather_RangeRulesAre400BeforeAnyRead()
    {
        (EnvReadService s, FakeReadStore store, _) = Make();

        await Assert.That(() => s.WeatherAsync(11, "2026-11-01", "2026-12-02", default)).Throws<InvalidRequestException>();
        await Assert.That(() => s.WeatherAsync(11, "2026-11-20", "2026-11-19", default)).Throws<InvalidRequestException>();
        await Assert.That(() => s.WeatherAsync(11, "25/11/2026", null, default)).Throws<InvalidRequestException>();
        await Assert.That(store.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Weather_DayCarriesPressureMmAndState()
    {
        (EnvReadService s, FakeReadStore store, _) = Make();
        store.Weather.Add(Day(11, Today, 755.0m, "FRONT"));
        store.Weather.Add(Day(11, Today.AddDays(1), 760.1m));

        StationWeatherDto dto = await s.WeatherAsync(11, "2026-11-25", "2026-11-26", default);

        await Assert.That(dto.Days.Count).IsEqualTo(2);
        await Assert.That(dto.Days[0].PressureMm).IsEqualTo(755.0m);
        await Assert.That(dto.Days[0].Pressure!.State).IsEqualTo("FRONT");
        await Assert.That(dto.Days[0].Time).IsEqualTo("12:00:00");
        await Assert.That(dto.Days[1].Pressure).IsNull();
    }

    [Test]
    public async Task LakeStations_ByMli_AnyCaseGuid_UnknownLakeEmpty()
    {
        (EnvReadService s, _, _) = Make();

        LakeStationsDto dto = s.LakeStations(Lake.ToUpperInvariant());
        LakeStationsDto none = s.LakeStations(Guid.Empty.ToString());

        await Assert.That(string.Join(",", dto.Stations.Select(x => x.Mli))).IsEqualTo("A01,B02");
        await Assert.That(dto.LakeName).IsEqualTo("Test River");
        await Assert.That(none.Stations.Count).IsEqualTo(0);
        await Assert.That(none.LakeName).IsNull();
        await Assert.That(() => s.LakeStations("not-a-guid")).Throws<InvalidRequestException>();
    }

    [Test]
    public async Task LakeWeather_EachStationWithCurrentAndTheDaysWeather()
    {
        (EnvReadService s, FakeReadStore store, _) = Make();
        store.Current.Add(new StationStateRow(12, "B02", Now, 3, null, null, null, 0.5, null, null, null, null, null, null, null));
        store.Weather.Add(Day(11, Today.AddDays(-3), 750.0m));
        store.Weather.Add(Day(11, Today, 755.0m));

        LakeWeatherDto dto = await s.LakeWeatherAsync(Lake, "2026-11-22", default);

        await Assert.That(dto.Date).IsEqualTo("2026-11-22");
        await Assert.That(dto.Stations[0].Station.Mli).IsEqualTo("A01");
        await Assert.That(dto.Stations[0].Weather!.PressureMm).IsEqualTo(750.0m);
        await Assert.That(dto.Stations[0].Current).IsNull();
        await Assert.That(dto.Stations[1].Weather).IsNull();
        await Assert.That(dto.Stations[1].Current!.Discharge).IsEqualTo(3);
    }

    [Test]
    public async Task LakeWeather_DateOutsideTheKeptWindowIs400()
    {
        (EnvReadService s, _, _) = Make();

        await Assert.That(() => s.LakeWeatherAsync(Lake, "2026-11-09", default)).Throws<InvalidRequestException>();
        await Assert.That(() => s.LakeWeatherAsync(Lake, "2026-12-11", default)).Throws<InvalidRequestException>();
    }

    [Test]
    public async Task Json_IsCamelCaseWithPressureInMm()
    {
        (EnvReadService s, FakeReadStore store, _) = Make();
        store.Weather.Add(Day(11, Today, 755.0m, "FRONT"));

        StationWeatherDto dto = await s.WeatherAsync(11, "2026-11-25", "2026-11-25", default);
        string json = JsonSerializer.Serialize(ApiResponse<StationWeatherDto>.Ok(dto), JsonSerializerOptions.Web);

        await Assert.That(json).Contains("\"pressureMm\":755.0").And.Contains("\"pressure\":\"mmHg\"")
            .And.Contains("\"tempHighC\":5").And.Contains("\"date\":\"2026-11-25\"").And.DoesNotContain("hpa");
    }

    [Test]
    public async Task Cache_FailedRefreshKeepsTheLastSnapshot()
    {
        var cache = new StationCache();
        var store = new ThrowingReadStore();
        var job = new StationCacheJob(store, cache, new EnvService.Jobs.JobStatus(), new FakeTimeProvider(Now),
            Microsoft.Extensions.Options.Options.Create(new EnvService.Configuration.EnvOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StationCacheJob>.Instance);
        cache.Load([Station(11, "A01", Lake)], Now);

        EnvService.Jobs.JobRun? run = await job.RunOnceAsync(default);

        await Assert.That(run!.LastOk).IsEqualTo(false);
        await Assert.That(cache.Current!.Count).IsEqualTo(1);
    }

    private sealed class ThrowingReadStore : IEnvReadStore
    {
        public Task<IReadOnlyList<StationInfo>> StationInfosAsync(CancellationToken ct) => throw new TimeoutException("down");

        public Task<IReadOnlyList<StationStateRow>> CurrentAsync(IReadOnlyCollection<int> sids, CancellationToken ct) =>
            throw new TimeoutException("down");

        public Task<IReadOnlyList<WeatherDayRow>> WeatherAsync(
            IReadOnlyCollection<int> sids, DateOnly from, DateOnly to, CancellationToken ct) => throw new TimeoutException("down");

        public Task<IReadOnlyList<WaterDayRow>> WaterDaysAsync(int sid, DateOnly from, DateOnly to, CancellationToken ct) =>
            throw new TimeoutException("down");
    }
}
