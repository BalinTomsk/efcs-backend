using System.Text.Json.Nodes;
using EnvService.Data;
using EnvService.Reads;
using EnvService.Web;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;

namespace EnvService.Tests;

internal sealed class FakeForecastStore : IForecastStore
{
    public string? Stations;
    public string? Reasoning;
    public string? Meta;
    public int Calls;
    public (Guid Fish, double Lat, double Lon, int Radius, string? Country, int Limit)? LastStations;
    public DateTime? LastAt;

    public Task<string?> StationsJsonAsync(Guid fishId, double lat, double lon, int radiusKm, string? country, int limit, CancellationToken ct)
    {
        Calls++;
        LastStations = (fishId, lat, lon, radiusKm, country, limit);
        return Task.FromResult(Stations);
    }

    public Task<string?> ReasoningJsonAsync(int sid, Guid fishId, DateTime atUtc, CancellationToken ct)
    {
        Calls++;
        LastAt = atUtc;
        return Task.FromResult(Reasoning);
    }

    public Task<string?> PlotMetaJsonAsync(int sid, Guid fishId, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Meta);
    }
}

public class ForecastServiceTests
{
    private const string Fish = "2cffb500-3e59-4120-9460-055856e9ac5c";
    private static readonly DateTime Now = new(2026, 11, 25, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 25);

    private const string ReasoningJson =
        "{\"sid\":12,\"today\":61,\"pressure\":{\"state\":\"FRONT\",\"mm\":759.8},\"week\":[" +
        "{\"date\":\"2026-11-25\",\"probability\":61},{\"date\":\"2026-11-26\",\"probability\":55}," +
        "{\"date\":\"2026-11-27\",\"probability\":50},{\"date\":\"2026-11-28\",\"probability\":50}," +
        "{\"date\":\"2026-11-29\",\"probability\":50},{\"date\":\"2026-11-30\",\"probability\":50}," +
        "{\"date\":\"2026-12-01\",\"probability\":50}]}";

    private static WeatherDayRow Day(DateOnly dt, decimal? mm, double? high = 5.26, int? rain = 2) => new(
        12, dt, null, high, -1.75, 3, 4, 1.5, 0.5, rain, 40, 80, 12.34, 270, "W", mm, 61, "rain", "Rain", "Light rain",
        2, Now, null, null, null, null, null);

    private static (ForecastService S, FakeForecastStore Fish, FakeReadStore Env) Make()
    {
        var fish = new FakeForecastStore { Reasoning = ReasoningJson, Meta = "{\"sid\":12,\"place\":\"Roseau R\",\"fish\":\"Walleye\",\"country\":\"US\",\"state\":\"MN\",\"lakeid\":\"15fb4adf-2892-e811-9104-00155d007b12\",\"lakename\":\"Roseau River\"}" };
        var env = new FakeReadStore();
        var cache = new StationCache();
        cache.Load([new StationInfo(12, "05112000", "x", "US", "MN", 48.9, -96.4, 0, 2, "ROSEAU", null, "", null, null, true, true,
            new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc), null)], Now);
        return (new ForecastService(fish, env, cache, new FakeTimeProvider(Now)), fish, env);
    }

    [Test]
    public async Task Stations_ValidatesAndClampsLikeDocapi()
    {
        (ForecastService s, FakeForecastStore fish, _) = Make();
        fish.Stations = "{\"total\":0,\"stations\":[]}";

        await s.StationsAsync(Fish.ToUpperInvariant(), "43.456", "-80.494", "900", "us", "0", default);

        await Assert.That(fish.LastStations).IsEqualTo((Guid.Parse(Fish), 43.46, -80.49, 500, (string?)"US", 50));
        await Assert.That(() => s.StationsAsync(null, "1", "1", null, null, null, default)).Throws<InvalidRequestException>();
        await Assert.That(() => s.StationsAsync(Fish, "91", "1", null, null, null, default)).Throws<InvalidRequestException>();
        await Assert.That(() => s.StationsAsync(Fish, "1", "1", null, "MX", null, default)).Throws<InvalidRequestException>();
    }

    [Test]
    public async Task Stations_UnknownFishIs404()
    {
        (ForecastService s, _, _) = Make();

        await Assert.That(() => s.StationsAsync(Fish, "1", "1", null, null, null, default)).Throws<NotFoundException>();
    }

    [Test]
    public async Task Reasoning_FillsWeatherOfDay0AndTheWeek()
    {
        (ForecastService s, _, FakeReadStore env) = Make();
        env.Weather.Add(Day(Today, 755.0m));
        env.Weather.Add(Day(Today.AddDays(1), 756.0m, high: null, rain: null));

        JsonNode doc = await s.ReasoningAsync("12", Fish, null, default);

        await Assert.That((string?)doc["weather"]!["conditions"]).IsEqualTo("Rain");
        await Assert.That((double?)doc["weather"]!["airHigh"]).IsEqualTo(5.3);
        await Assert.That((double?)doc["weather"]!["wind"]).IsEqualTo(12.3);
        await Assert.That((int?)doc["weather"]!["precipMm"]).IsEqualTo(2);
        await Assert.That((string?)doc["week"]![1]!["weather"]!["conditions"]).IsEqualTo("Rain");
        await Assert.That(doc["week"]![1]!["weather"]!.AsObject().ContainsKey("airHigh")).IsFalse();
        await Assert.That(doc["week"]![2]!.AsObject().ContainsKey("weather")).IsFalse();
    }

    [Test]
    public async Task Reasoning_DayMovesTheDateAndIsChecked()
    {
        (ForecastService s, FakeForecastStore fish, _) = Make();

        await s.ReasoningAsync("12", Fish, "3", default);

        await Assert.That(fish.LastAt).IsEqualTo(Now.AddDays(3));
        await Assert.That(() => s.ReasoningAsync("12", Fish, "7", default)).Throws<InvalidRequestException>();
        await Assert.That(() => s.ReasoningAsync("0", Fish, null, default)).Throws<InvalidRequestException>();
    }

    [Test]
    public async Task Reasoning_UnknownPairIs404()
    {
        (ForecastService s, FakeForecastStore fish, _) = Make();
        fish.Reasoning = null;

        await Assert.That(() => s.ReasoningAsync("12", Fish, null, default)).Throws<NotFoundException>();
    }

    [Test]
    public async Task Plot_SeriesSpan23DaysWithMmAndMeans()
    {
        (ForecastService s, _, FakeReadStore env) = Make();
        env.Water.Add(new WaterDayRow(12, Today.AddDays(-1), 24, 15.1234, 307.2384, null, null, null));
        env.Weather.Add(Day(Today, 755.0m));
        env.Weather.Add(Day(Today.AddDays(-1), 754.0m));

        JsonNode doc = await s.PlotAsync("12", Fish, default);
        JsonNode plot = doc["plot"]!;

        await Assert.That(plot["date"]!.AsArray().Count).IsEqualTo(23);
        await Assert.That((string?)plot["date"]![0]).IsEqualTo("Tue 10");
        await Assert.That((string?)plot["date"]![15]).IsEqualTo("Wed 25");
        await Assert.That((string?)plot["units"]!["pressure"]).IsEqualTo("mmHg");
        await Assert.That((decimal?)plot["pressure"]![15]).IsEqualTo(755.0m);
        await Assert.That((double?)plot["discharge"]![14]).IsEqualTo(15.123);
        await Assert.That((double?)plot["level"]![14]).IsEqualTo(307.238);
        await Assert.That(plot["discharge"]![15]).IsNull();
        // today = the air temperature, other days the day temperature; precipitation = (day + night) / 2
        await Assert.That((double?)plot["temperature"]![15]).IsEqualTo(4);
        await Assert.That((double?)plot["temperature"]![14]).IsEqualTo(3);
        await Assert.That((double?)plot["precipitation"]![15]).IsEqualTo(1.0);
        await Assert.That((string?)plot["stamp"]).IsEqualTo("2026-11-24 00:00:00.0000000");
        await Assert.That((string?)plot["place"]).IsEqualTo("Roseau R");
        await Assert.That(doc["week"]!.AsArray().Count).IsEqualTo(7);
        await Assert.That((string?)doc["fishId"]).IsEqualTo(Fish);
    }

    [Test]
    public async Task Plot_Calendar23RowsPlaceholdersAndTruncatedNumbers()
    {
        (ForecastService s, _, FakeReadStore env) = Make();
        env.Weather.Add(Day(Today.AddDays(-15), 750.0m));
        env.Weather.Add(Day(Today, 755.0m, high: -2.96));

        JsonNode doc = await s.PlotAsync("12", Fish, default);
        JsonArray cal = doc["calendar"]!.AsArray();

        await Assert.That(cal.Count).IsEqualTo(23);
        await Assert.That((string?)cal[0]!["dt"]).IsEqualTo("2026-11-10");
        await Assert.That((string?)cal[1]!["shortText"]).IsEqualTo("");
        await Assert.That((int?)cal[1]!["temperature_high"]).IsEqualTo(0);
        await Assert.That((int?)cal[15]!["temperature_high"]).IsEqualTo(-3);
        await Assert.That((int?)cal[15]!["temperature_low"]).IsEqualTo(-1);
        await Assert.That((int?)cal[15]!["wind_max_speed"]).IsEqualTo(12);
        await Assert.That((int?)cal[15]!["precipitation"]).IsEqualTo(2);
    }

    [Test]
    public async Task Plot_UnknownPairIs404()
    {
        (ForecastService s, FakeForecastStore fish, _) = Make();
        fish.Meta = null;

        await Assert.That(() => s.PlotAsync("12", Fish, default)).Throws<NotFoundException>();
    }

    [Test]
    public async Task Answers_AreCachedForTheSameArguments()
    {
        (ForecastService s, FakeForecastStore fish, _) = Make();

        JsonNode a = await s.ReasoningAsync("12", Fish, null, default);
        a["today"] = 0;
        JsonNode b = await s.ReasoningAsync("12", Fish, "0", default);

        await Assert.That(fish.Calls).IsEqualTo(1);
        await Assert.That((int?)b["today"]).IsEqualTo(61);
    }
}
