using System.Text.Json;
using EnvService.Migrator;
using TUnit.Core;

namespace EnvService.Migrator.Tests;

public class ConversionsTests
{
    private static WaterDataRow Water(double? discharge = null, int? pressure = null, int? ph = null, double? elevation = null,
        double? oxygen = null) =>
        new(7, new DateTime(2026, 11, 20, 10, 5, 0), discharge, elevation, null, oxygen, ph, null, pressure, null, null,
            null, null, null, null, null, null, null, null);

    [Test]
    public async Task Pressure_AboveNineHundredIsHpa_ConvertedToMmHg()
    {
        await Assert.That(Conversions.PressureMm(1013)).IsEqualTo(759.8);
        await Assert.That(Conversions.PressureMm(1007)).IsEqualTo(755.3);
    }

    [Test]
    public async Task Pressure_FourFiftyToEightHundredIsAlreadyMmHg()
    {
        // WaterData held mm Hg as sent before 2026-11-13 (local data: 704 .. 716 mm).
        await Assert.That(Conversions.PressureMm(704)).IsEqualTo(704.0);
        await Assert.That(Conversions.PressureMm(760)).IsEqualTo(760.0);
        await Assert.That(Conversions.PressureMm(800)).IsEqualTo(800.0);
    }

    [Test]
    public async Task Pressure_HighGaugeStationPressureInHpa_IsNotMistakenForMmHg()
    {
        // Utah's 09315000 sends station pressure 845..885 hPa; read as mm Hg it was an impossible 885 mm (bug found
        // by the first local migrator run, 2026-11-25).
        await Assert.That(Conversions.PressureMm(885)).IsEqualTo(663.8);
        await Assert.That(Conversions.PressureMm(845)).IsEqualTo(633.8);
    }

    [Test]
    public async Task Pressure_OutsideBothRangesIsNoPressure()
    {
        await Assert.That(Conversions.PressureMm(101)).IsNull();
        await Assert.That(Conversions.PressureMm(0)).IsNull();
        await Assert.That(Conversions.PressureMm(null)).IsNull();
    }

    [Test]
    public async Task Discharge_MinusNineNineNineIsNoReading()
    {
        Dictionary<string, object>? row = Conversions.WaterBackfillRow(Water(discharge: -999, elevation: 1.5));

        await Assert.That(row).IsNotNull();
        await Assert.That(row!.ContainsKey("discharge")).IsFalse();
        await Assert.That(row["elevation"]).IsEqualTo(1.5);
    }

    [Test]
    public async Task Ph_IsNeverCopied_BecauseWaterDataTruncatedIt()
    {
        Dictionary<string, object>? row = Conversions.WaterBackfillRow(Water(ph: 7, oxygen: 8.1));

        await Assert.That(row!.ContainsKey("ph")).IsFalse();
        await Assert.That(row["oxygen"]).IsEqualTo(8.1);
    }

    [Test]
    public async Task ARowCarryingNothing_IsDropped()
    {
        await Assert.That(Conversions.WaterBackfillRow(Water(discharge: -999, ph: 7))).IsNull();
    }

    [Test]
    public async Task WaterRow_HasSidTimeAndMmPressure()
    {
        string json = JsonSerializer.Serialize(Conversions.WaterBackfillRow(Water(discharge: 2.5, pressure: 1013)));

        await Assert.That(json).IsEqualTo("{\"sid\":7,\"ts\":\"2026-11-20 10:05:00\",\"discharge\":2.5,\"pressure_mm\":759.8}");
    }

    [Test]
    public async Task ForecastEnvelope_IsTheCanonicalV1Shape_InHpa()
    {
        var day = new ForecastRow("02HB004", 2, new DateOnly(2026, 11, 24), new TimeSpan(12, 0, 0), 5.5, -1, null, 3,
            0, 0, 80, 12, 270, "W", 1013, 40, 2, 61, "rain", "Rain", "Light rain");

        using JsonDocument doc = JsonDocument.Parse(
            Conversions.ForecastEnvelope("02HB004", 2, [day], new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero)));
        JsonElement root = doc.RootElement;
        JsonElement d = root.GetProperty("days")[0];

        await Assert.That(root.GetProperty("schema").GetString()).IsEqualTo("fishfind.weather.forecast/v1");
        await Assert.That(root.GetProperty("providerType").GetInt32()).IsEqualTo(2);
        await Assert.That(d.GetProperty("date").GetString()).IsEqualTo("2026-11-24");
        await Assert.That(d.GetProperty("time").GetString()).IsEqualTo("12:00:00");
        await Assert.That(d.GetProperty("pressureHpa").GetInt32()).IsEqualTo(1013);
        await Assert.That(d.GetProperty("tempHighC").GetDouble()).IsEqualTo(5.5);
        await Assert.That(d.TryGetProperty("tempDayC", out _)).IsFalse();
    }

    [Test]
    public async Task ForecastEnvelope_ANullTimeIsMidnight()
    {
        var day = new ForecastRow("x", 1, new DateOnly(2026, 11, 24), null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null, null);

        using JsonDocument doc = JsonDocument.Parse(Conversions.ForecastEnvelope("x", 1, [day], DateTimeOffset.UtcNow));

        await Assert.That(doc.RootElement.GetProperty("days")[0].GetProperty("time").GetString()).IsEqualTo("00:00:00");
    }

    [Test]
    public async Task StationMirrorRow_TrimsCountryAndCarriesHasFish()
    {
        var s = new StationRow(12, "02HB004", Guid.Parse("11111111-1111-1111-1111-111111111111"), "CA ", " ", 43.5, -79.6,
            -5, 2, "Credit R", null, "Credit River", 120, "ECCC", true, false, new DateTime(2026, 1, 1));

        Dictionary<string, object?> row = Conversions.StationMirrorRow(s);

        await Assert.That(row["country"]).IsEqualTo("CA");
        await Assert.That(row["state"]).IsNull();
        await Assert.That(row["hasFish"]).IsEqualTo(1);
        await Assert.That(row["supported"]).IsEqualTo(0);
        await Assert.That(row["lakeId"]).IsNull();
    }
}
