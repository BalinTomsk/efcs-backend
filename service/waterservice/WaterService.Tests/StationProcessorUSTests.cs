using TUnit.Core;
using WaterService.Domain;
using WaterService.Processing;
using WaterService.Sources;

namespace WaterService.Tests;

public class StationProcessorUSTests
{
    private static readonly TimeZoneInfo Mountain = UsgsWaterDataClient.ResolveTimeZone("MST", "Y");

    private static UsgsObservation Obs(string code, string time, string value, string? statistic = "00011") =>
        new(code, statistic, DateTimeOffset.Parse(time), value);

    [Test]
    public async Task BuildReadings_UsesLegacyNameAndUnit()
    {
        // sp_push_us_water_data keys its unit conversion on the exact legacy unit string ('ft^3/s').
        List<UsSeriesReading> series = StationProcessorUS.BuildReadings(
            [Obs("00060", "2026-10-11T18:00:00+00:00", "12.5")], Mountain);

        await Assert.That(series).HasCount().EqualTo(1);
        await Assert.That(series[0].Name).IsEqualTo("Streamflow");
        await Assert.That(series[0].Unit).IsEqualTo("ft^3/s");
        await Assert.That(series[0].XmlDoc).IsEqualTo("<root><a d=\"2026-10-11\" v=\"12.5\" /></root>");
    }

    [Test]
    public async Task BuildReadings_DatesByTheStationsLocalDay()
    {
        // 03:00 UTC on Oct 11 is still Oct 10 in New Mexico (MDT, UTC-6). Dating it by UTC would write a
        // reading into tomorrow's row.
        List<UsSeriesReading> series = StationProcessorUS.BuildReadings(
            [Obs("00065", "2026-10-11T03:00:00+00:00", "3.34")], Mountain);

        await Assert.That(series[0].XmlDoc).IsEqualTo("<root><a d=\"2026-10-10\" v=\"3.34\" /></root>");
    }

    [Test]
    public async Task BuildReadings_DropsUnmappedNonInstantaneousAndNonNumeric()
    {
        List<UsSeriesReading> series = StationProcessorUS.BuildReadings(
            [
                Obs("99999", "2026-10-11T18:00:00+00:00", "410"),          // not a legacy parameter
                Obs("00060", "2026-10-11T18:00:00+00:00", "12", "00003"), // daily mean, not instantaneous
                Obs("00065", "2026-10-11T18:00:00+00:00", "Ice"),
                Obs("00010", "2026-10-11T18:00:00+00:00", "17.2", null),  // no statistic: kept
            ],
            Mountain);

        await Assert.That(series).HasCount().EqualTo(1);
        await Assert.That(series[0].Name).IsEqualTo("Temperature");
        await Assert.That(series[0].Unit).IsEqualTo("water, &#176;C");
    }

    [Test]
    public async Task ResolveTimeZone_HonoursTheDaylightSavingFlag()
    {
        var july = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

        await Assert.That(UsgsWaterDataClient.ResolveTimeZone("MST", "Y").GetUtcOffset(july))
            .IsEqualTo(TimeSpan.FromHours(-6));
        await Assert.That(UsgsWaterDataClient.ResolveTimeZone("MST", "N").GetUtcOffset(july)) // Arizona
            .IsEqualTo(TimeSpan.FromHours(-7));
        await Assert.That(UsgsWaterDataClient.ResolveTimeZone("HST", "N").GetUtcOffset(july))
            .IsEqualTo(TimeSpan.FromHours(-10));
        await Assert.That(UsgsWaterDataClient.ResolveTimeZone(null, null)).IsEqualTo(TimeZoneInfo.Utc);
    }
}
