using System.Text.Json.Nodes;
using TUnit.Core;
using WeatherService.Canonical;

namespace WeatherService.Tests;

/// <summary>
/// The expected numbers are the ones prod produced for MLI 13068500 through the T-SQL branch, and they
/// match <c>VisualCrossingConverterTest</c> / <c>OpenMeteoConverterTest</c> in the Java service, which is
/// the reference implementation. A divergence between the two ports — or between either port and the
/// legacy database path — fails the build rather than showing up as changed weather on a page.
/// </summary>
public class ForecastConverterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-13T04:12:00Z");

    private static VisualCrossingConverter VisualCrossing() =>
        new(new FakeTimeProvider(Now));

    private static OpenMeteoConverter OpenMeteo() =>
        new(new FakeTimeProvider(Now));

    private static string VisualCrossingPayload(params string[] days) =>
        "{\"queryCost\":1,\"latitude\":43.13,\"longitude\":-112.47,"
        + "\"timezone\":\"America/Boise\",\"days\":[" + string.Join(",", days) + "]}";

    private static string Day(string date, string extra) =>
        "{\"datetime\":\"" + date + "\"," + extra + "}";

    // ---------------------------------------------------------------- Visual Crossing

    [Test]
    public async Task VisualCrossing_ConvertsUsUnitsToMetric()
    {
        string raw = VisualCrossingPayload(Day("2026-08-13",
            "\"tempmax\":85.0,\"tempmin\":62.0,\"temp\":74.1,\"humidity\":39.7,"
            + "\"precip\":0.0,\"precipprob\":6.0,\"windspeed\":12.8,\"winddir\":201.2,"
            + "\"pressure\":1009.7,\"conditions\":\"Partially cloudy\","
            + "\"description\":\"Partly cloudy throughout the day.\",\"icon\":\"partly-cloudy-day\""));

        ForecastDay day = VisualCrossing().Convert(raw, "13068500").Days[0];

        // 85F = 29.44C, 62F = 16.67C, 74.1F = 23.4C -> 23, 12.8mph = 20.6km/h
        await Assert.That(day.TempHighC!.Value).IsBetween(29.43, 29.45);
        await Assert.That(day.TempLowC!.Value).IsBetween(16.66, 16.68);
        await Assert.That(day.TempC).IsEqualTo(23);
        await Assert.That(day.WindSpeedKmh!.Value).IsBetween(20.55, 20.65);
        await Assert.That(day.WindDirection).IsEqualTo("S");
        await Assert.That(day.PressureHpa).IsEqualTo(1010);
        await Assert.That(day.PrecipChancePct).IsEqualTo(6);
        await Assert.That(day.WeatherCode).IsEqualTo(2);
        await Assert.That(day.Icon).IsEqualTo("om_2.png");
        await Assert.That(day.ConditionsShort).IsEqualTo("Partially cloudy");
        await Assert.That(day.Time).IsEqualTo(ForecastDay.DailySummaryTime);
    }

    [Test]
    public async Task VisualCrossing_ConvertsInchesAndSplitsRainEvenly()
    {
        string raw = VisualCrossingPayload(Day("2026-08-13",
            "\"tempmax\":72.1,\"tempmin\":58.9,\"temp\":65.1,\"precip\":0.5,"
            + "\"precipprob\":75.0,\"windspeed\":9.2,\"winddir\":180.4,\"pressure\":1008.9,"
            + "\"conditions\":\"Rain\",\"description\":\"Rain.\",\"icon\":\"rain\""));

        ForecastDay day = VisualCrossing().Convert(raw, "13068500").Days[0];

        // 0.5in = 12.7mm -> 13, split evenly because a daily document has no hourly resolution
        await Assert.That(day.PrecipMm).IsEqualTo(13);
        await Assert.That(day.PrecipDayMm!.Value).IsBetween(6.34, 6.36);
        await Assert.That(day.PrecipNightMm!.Value).IsBetween(6.34, 6.36);
        await Assert.That(day.WeatherCode).IsEqualTo(63);
    }

    [Test]
    public async Task VisualCrossing_ClipsToTodayThroughSixDaysAhead()
    {
        string raw = VisualCrossingPayload(
            Day("2026-08-12", "\"tempmax\":94.7,\"tempmin\":53.7,\"icon\":\"clear-day\""),   // yesterday
            Day("2026-08-13", "\"tempmax\":85.0,\"tempmin\":62.0,\"icon\":\"clear-day\""),
            Day("2026-08-19", "\"tempmax\":81.6,\"tempmin\":53.1,\"icon\":\"clear-day\""),   // today+6
            Day("2026-08-20", "\"tempmax\":76.7,\"tempmin\":49.5,\"icon\":\"clear-day\""));  // beyond

        CanonicalForecast forecast = VisualCrossing().Convert(raw, "13068500");

        await Assert.That(forecast.Days.Count).IsEqualTo(2);
        await Assert.That(forecast.Days[0].Date).IsEqualTo(new DateOnly(2026, 8, 13));
        await Assert.That(forecast.Days[1].Date).IsEqualTo(new DateOnly(2026, 8, 19));
    }

    [Test]
    public async Task VisualCrossing_EmbedsProviderDocumentAndStampsProvenance()
    {
        string raw = VisualCrossingPayload(
            Day("2026-08-13", "\"tempmax\":85.0,\"tempmin\":62.0,\"icon\":\"clear-day\""));

        CanonicalForecast forecast = VisualCrossing().Convert(raw, "13068500");

        await Assert.That(forecast.SchemaVersion).IsEqualTo("fishfind.weather.forecast/v1");
        await Assert.That(forecast.Provider).IsEqualTo("visual-crossing");
        await Assert.That(forecast.ProviderType).IsEqualTo(WeatherSourceType.VisualCrossing);
        await Assert.That(forecast.Mli).IsEqualTo("13068500");
        await Assert.That(forecast.FetchedUtc).IsEqualTo(Now);
        // the raw document survives, which is what makes a stored payload replayable
        await Assert.That(forecast.Raw!["queryCost"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task VisualCrossing_UnknownIconFallsBackWithoutLosingTheDay()
    {
        string raw = VisualCrossingPayload(
            Day("2026-08-13", "\"tempmax\":85.0,\"tempmin\":62.0,\"icon\":\"meteor-shower\""));

        ForecastDay day = VisualCrossing().Convert(raw, "13068500").Days[0];

        await Assert.That(day.WeatherCode).IsNull();
        await Assert.That(day.Icon).IsEqualTo("om_na.png");
        await Assert.That(day.TempHighC).IsNotNull();
    }

    [Test]
    public async Task VisualCrossing_ThrowsWhenDocumentIsNotVisualCrossing()
    {
        var converter = VisualCrossing();

        var ex = Assert.Throws<ForecastConversionException>(
            () => converter.Convert("{\"observations\":[{\"stationID\":\"KX\"}]}", "13068500"));

        await Assert.That(ex!.Message).Contains("no days[]");
    }

    [Test]
    public async Task VisualCrossing_ThrowsWhenEveryDayIsInThePast()
    {
        var converter = VisualCrossing();
        string raw = VisualCrossingPayload(
            Day("2026-08-01", "\"tempmax\":85.0,\"tempmin\":62.0,\"icon\":\"clear-day\""));

        var ex = Assert.Throws<ForecastConversionException>(() => converter.Convert(raw, "13068500"));

        await Assert.That(ex!.Message).Contains("entirely in the past");
    }

    // ---------------------------------------------------------------- Open-Meteo

    private const string OpenMeteoPayload = """
        {"hourly":{
           "time":["2026-08-13T22:00","2026-08-13T23:00"],
           "temperature_2m":[15.0,16.0],
           "relative_humidity_2m":[80,82],
           "precipitation_probability":[10,20],
           "pressure_msl":[1010,1011],
           "wind_speed_10m":[5.0,5.5],
           "wind_direction_10m":[180,191],
           "weather_code":[0,0],
           "rain":[0.0,0.0]},
         "daily":{"time":["2026-08-13"],
           "temperature_2m_max":[24.7],"temperature_2m_min":[11.8]},
         "timezone":"America/Los_Angeles"}
        """;

    [Test]
    public async Task OpenMeteo_LatestHourOfTheDayWins()
    {
        ForecastDay day = OpenMeteo().Convert(OpenMeteoPayload, "01015800").Days[0];

        await Assert.That(day.Time).IsEqualTo("23:00:00");
        await Assert.That(day.TempC).IsEqualTo(16);          // the 23:00 hour, not the 22:00 one
        await Assert.That(day.HumidityPct).IsEqualTo(82.0);
        await Assert.That(day.WindSpeedKmh).IsEqualTo(5.5);
        await Assert.That(day.WindDirection).IsEqualTo("S");
        await Assert.That(day.PressureHpa).IsEqualTo(1011);
        await Assert.That(day.TempHighC).IsEqualTo(24.7);    // from the daily arrays
        await Assert.That(day.TempLowC).IsEqualTo(11.8);
        await Assert.That(day.Icon).IsEqualTo("om_0.png");
        await Assert.That(day.ConditionsShort).IsEqualTo("Clear");
        await Assert.That(day.ConditionsLong).IsEqualTo("Clear sky");
    }

    [Test]
    public async Task OpenMeteo_RainSplitsAtTheDaytimeBoundaryAndDaytimeTempIsAMean()
    {
        const string raw = """
            {"hourly":{
               "time":["2026-08-13T05:00","2026-08-13T06:00","2026-08-13T17:00","2026-08-13T18:00"],
               "temperature_2m":[9.0,10.0,20.0,21.0],
               "rain":[1.0,2.0,3.0,4.0],
               "weather_code":[61,61,61,61]},
             "daily":{"time":["2026-08-13"],
               "temperature_2m_max":[21.0],"temperature_2m_min":[9.0]}}
            """;

        ForecastDay day = OpenMeteo().Convert(raw, "01015800").Days[0];

        // 06:00 and 17:00 are daytime (2.0 + 3.0); 05:00 and 18:00 are not (1.0 + 4.0)
        await Assert.That(day.PrecipDayMm!.Value).IsBetween(4.99, 5.01);
        await Assert.That(day.PrecipNightMm!.Value).IsBetween(4.99, 5.01);
        await Assert.That(day.PrecipMm).IsEqualTo(10);
        await Assert.That(day.TempDayC!.Value).IsBetween(14.99, 15.01);   // mean of 10.0 and 20.0
    }

    [Test]
    public async Task OpenMeteo_EmitsOneRowPerDay()
    {
        const string raw = """
            {"hourly":{
               "time":["2026-08-13T12:00","2026-08-13T13:00","2026-08-14T12:00"],
               "temperature_2m":[15.0,16.0,17.0],
               "rain":[0.0,0.0,0.0],
               "weather_code":[2,2,3]},
             "daily":{"time":["2026-08-13","2026-08-14"],
               "temperature_2m_max":[20.0,21.0],"temperature_2m_min":[10.0,11.0]}}
            """;

        CanonicalForecast forecast = OpenMeteo().Convert(raw, "01015800");

        await Assert.That(forecast.Days.Count).IsEqualTo(2);
        await Assert.That(forecast.Days[0].TempC).IsEqualTo(16);      // 13:00 beats 12:00
        await Assert.That(forecast.Days[1].TempHighC).IsEqualTo(21.0);
        await Assert.That(forecast.Days[1].WeatherCode).IsEqualTo(3);
    }

    [Test]
    public async Task OpenMeteo_ThrowsWhenDocumentIsNotOpenMeteo()
    {
        var converter = OpenMeteo();

        var ex = Assert.Throws<ForecastConversionException>(
            () => converter.Convert("{\"days\":[{\"datetime\":\"2026-08-13\"}]}", "01015800"));

        await Assert.That(ex!.Message).Contains("no hourly.time[]");
    }

    // ---------------------------------------------------------------- the envelope itself

    [Test]
    public async Task Envelope_SerialisesTheMemberNamesTheDatabaseReads()
    {
        // sp_ows_meteo_canonical reads these paths verbatim; a renamed member is a silent data loss,
        // so the names are asserted rather than left to a serializer naming policy.
        string raw = VisualCrossingPayload(Day("2026-08-13",
            "\"tempmax\":85.0,\"tempmin\":62.0,\"temp\":74.1,\"humidity\":39.7,"
            + "\"precip\":0.0,\"precipprob\":6.0,\"windspeed\":12.8,\"winddir\":201.2,"
            + "\"pressure\":1009.7,\"conditions\":\"Clear\",\"description\":\"Clear.\","
            + "\"icon\":\"clear-day\""));

        string json = VisualCrossing().Convert(raw, "13068500").ToJson();
        JsonNode envelope = JsonNode.Parse(json)!;

        await Assert.That(envelope["schema"]!.GetValue<string>()).IsEqualTo("fishfind.weather.forecast/v1");
        await Assert.That(envelope["provider"]!.GetValue<string>()).IsEqualTo("visual-crossing");
        await Assert.That(envelope["providerType"]!.GetValue<int>()).IsEqualTo(4);
        await Assert.That(envelope["mli"]!.GetValue<string>()).IsEqualTo("13068500");

        JsonNode day = envelope["days"]![0]!;
        await Assert.That(day["date"]!.GetValue<string>()).IsEqualTo("2026-08-13");
        await Assert.That(day["time"]!.GetValue<string>()).IsEqualTo("00:00:00");
        await Assert.That(day["tempHighC"]).IsNotNull();
        await Assert.That(day["tempLowC"]).IsNotNull();
        await Assert.That(day["tempC"]).IsNotNull();
        await Assert.That(day["humidityPct"]).IsNotNull();
        await Assert.That(day["windSpeedKmh"]).IsNotNull();
        await Assert.That(day["windDegrees"]).IsNotNull();
        await Assert.That(day["windDirection"]).IsNotNull();
        await Assert.That(day["pressureHpa"]).IsNotNull();
        await Assert.That(day["precipChancePct"]).IsNotNull();
        await Assert.That(day["precipMm"]).IsNotNull();
        await Assert.That(day["precipDayMm"]).IsNotNull();
        await Assert.That(day["precipNightMm"]).IsNotNull();
        await Assert.That(day["weatherCode"]).IsNotNull();
        await Assert.That(day["icon"]).IsNotNull();
        await Assert.That(day["conditionsShort"]).IsNotNull();
        await Assert.That(day["conditionsLong"]).IsNotNull();
        await Assert.That(envelope["raw"]).IsNotNull();
    }

    [Test]
    public async Task Envelope_OmitsNullsSoAMissingReadingStaysMissing()
    {
        // OPENJSON yields NULL for an absent member; emitting 0 instead would fabricate a reading.
        string raw = VisualCrossingPayload(
            Day("2026-08-13", "\"tempmax\":85.0,\"tempmin\":62.0,\"icon\":\"clear-day\""));

        string json = VisualCrossing().Convert(raw, "13068500").ToJson();
        JsonNode day = JsonNode.Parse(json)!["days"]![0]!;

        await Assert.That(day["humidityPct"]).IsNull();
        await Assert.That(day["windSpeedKmh"]).IsNull();
        await Assert.That(day["precipChancePct"]).IsNull();
    }

    /// <summary>Minimal fixed clock; the converters only ever ask for the current instant.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
