using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Core;
using WeatherService.Canonical;
using WeatherService.Configuration;
using WeatherService.Domain;
using WeatherService.Processing;
using WeatherService.Sources;
using static WeatherService.Tests.TestSupport;

namespace WeatherService.Tests;

/// <summary>
/// Open-Meteo pressure for providers whose forecast has none (Weather.gov): the pure day mapping, and
/// that the backfill is best-effort — off, out of budget or failing, the station is still saved.
/// </summary>
public class PressureBackfillTests
{
    private static readonly StationRef Subject = Station("MLI-1", 47.5, -122.3, "WA");

    private const string PressureDocument = """
        {"hourly":{"time":["2026-08-13T22:00","2026-08-13T23:00","2026-08-14T00:00","2026-08-14T23:00"],
                   "pressure_msl":[1004.6,1003.5,1001.0,null]}}
        """;

    [Test]
    public async Task DailyPressure_TakesTheLastHourOfEachDayRounded()
    {
        IReadOnlyDictionary<DateOnly, int> byDay = PressureFill.DailyPressure(PressureDocument);

        // 13th: 23:00 = 1003.5 -> 1004 (away from zero); 14th: the 23:00 value is null, so 00:00 = 1001
        await Assert.That(byDay[new DateOnly(2026, 8, 13)]).IsEqualTo(1004);
        await Assert.That(byDay[new DateOnly(2026, 8, 14)]).IsEqualTo(1001);
    }

    [Test]
    public async Task DailyPressure_IgnoresADocumentWithoutPressure()
    {
        await Assert.That(PressureFill.DailyPressure("""{"hourly":{"time":["2026-08-13T23:00"]}}""")).IsEmpty();
    }

    [Test]
    public async Task Fill_KeepsExistingPressureAndFillsOnlyMissingDays()
    {
        CanonicalForecast forecast = Forecast(
            Day(new DateOnly(2026, 8, 13), 1010), Day(new DateOnly(2026, 8, 14), null), Day(new DateOnly(2026, 8, 15), null));

        CanonicalForecast filled = PressureFill.Fill(forecast, PressureFill.DailyPressure(PressureDocument));

        await Assert.That(filled.Days[0].PressureHpa).IsEqualTo(1010);
        await Assert.That(filled.Days[1].PressureHpa).IsEqualTo(1001);
        await Assert.That(filled.Days[2].PressureHpa).IsNull();
    }

    [Test]
    public async Task WeatherGov_SavesOpenMeteoPressureInTheEnvelope()
    {
        var repository = new FakeWeatherDataRepository();
        var openMeteo = new StubHandler(_ => Ok(PressureDocument));
        var processor = WeatherGovProcessor(repository, Backfill(openMeteo));

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.Processed);

        JsonNode day = JsonNode.Parse(repository.Saved[0].Json)!["days"]![0]!;
        await Assert.That(day["pressureHpa"]!.GetValue<int>()).IsEqualTo(1004);
        await Assert.That(openMeteo.LastRequest!.RequestUri!.Query).Contains("hourly=pressure_msl");
    }

    [Test]
    public async Task WeatherGov_StillSavesWhenOpenMeteoFails()
    {
        var repository = new FakeWeatherDataRepository();
        var processor = WeatherGovProcessor(
            repository, Backfill(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))));

        await Assert.That(await processor.ProcessAsync(Subject)).IsEqualTo(ProcessingOutcome.Processed);
        await Assert.That(repository.Saved).HasCount().EqualTo(1);
        await Assert.That(JsonNode.Parse(repository.Saved[0].Json)!["days"]![0]!["pressureHpa"]).IsNull();
    }

    [Test]
    public async Task Backfill_MakesNoCallWhenDisabledOrOutOfBudget()
    {
        CanonicalForecast forecast = Forecast(Day(new DateOnly(2026, 8, 13), null));
        var off = new StubHandler(_ => Ok(PressureDocument));
        var noBudget = new StubHandler(_ => Ok(PressureDocument));

        await Backfill(off, o => o.PressureBackfill.Enabled = false).FillAsync(forecast, Subject);
        await Backfill(noBudget, o => o.PressureBackfill.DailyLimit = 0).FillAsync(forecast, Subject);

        await Assert.That(off.RequestCount).IsEqualTo(0);
        await Assert.That(noBudget.RequestCount).IsEqualTo(0);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static PressureBackfill Backfill(StubHandler openMeteo, Action<WorkerOptions>? customise = null)
    {
        OpenMeteoFetcher fetcher = Fetcher(openMeteo,
            (f, o, p, l) => new OpenMeteoFetcher(f, o, p, l, NullLogger<OpenMeteoFetcher>.Instance), customise);
        var tracker = new WeatherApiUsageTracker(
            Options.Create(new LifecycleOptions
            {
                StateDir = Path.Combine(Path.GetTempPath(), "weather-tests", Guid.NewGuid().ToString("N"))
            }),
            NullLogger<WeatherApiUsageTracker>.Instance);
        return new PressureBackfill(fetcher, tracker, WorkerConfig(customise), NullLogger<PressureBackfill>.Instance);
    }

    private static StationProcessorWeatherGov WeatherGovProcessor(FakeWeatherDataRepository repository, PressureBackfill backfill) =>
        new(Fetcher(
                new StubHandler(request => request.RequestUri!.AbsolutePath.Contains("/points/")
                    ? Ok("""{"properties":{"forecast":"https://api.weather.gov/gridpoints/BOI/62,61/forecast"}}""")
                    : Ok(GridpointForecast)),
                (f, o, p, l) => new WeatherGovFetcher(f, o, p, l, NullLogger<WeatherGovFetcher>.Instance)),
            new WeatherGovStationResolver(null!, null!, NullLogger<WeatherGovStationResolver>.Instance),
            repository, new WeatherGovConverter(), NullLogger<StationProcessorWeatherGov>.Instance, backfill);

    private const string GridpointForecast = """
        {"properties":{"units":"si","periods":[
          {"number":1,"name":"Today","startTime":"2026-08-13T06:00:00-06:00","isDaytime":true,
           "temperature":29,"temperatureUnit":"C","probabilityOfPrecipitation":{"value":20},
           "relativeHumidity":{"value":41},"windSpeed":"10 to 15 km/h","windDirection":"SSW",
           "icon":"https://api.weather.gov/icons/land/day/sct?size=medium",
           "shortForecast":"Mostly Sunny","detailedForecast":"Mostly sunny."}]}}
        """;

    private static CanonicalForecast Forecast(params ForecastDay[] days) => new()
    {
        Provider = "weather-gov", ProviderType = WeatherSourceType.WeatherGov, Mli = "MLI-1",
        FetchedUtc = DateTimeOffset.UnixEpoch, Days = days,
    };

    private static ForecastDay Day(DateOnly date, int? hpa) =>
        new() { Date = date, Time = ForecastDay.DailySummaryTime, PressureHpa = hpa };

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
