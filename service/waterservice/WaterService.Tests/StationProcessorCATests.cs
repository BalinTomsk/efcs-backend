using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using WaterService.Domain;
using WaterService.Processing;
using WaterService.Web;

namespace WaterService.Tests;

public class StationProcessorCATests
{
    private static StationProcessorCA NewProcessor() =>
        new(fetcher: null!, dataRepo: null!, metrics: new WaterMetrics(), log: NullLogger<StationProcessorCA>.Instance);

    private const string Header = "ID,Date,Level,Grade,Symbol,QAQC,Discharge";

    [Test]
    public async Task Parse_ValidRow_MapsColumns()
    {
        string csv = Header + "\n05BB001,2024-06-01T05:00:00-06:00,1.234,,,,45.6";

        List<Reading> readings = NewProcessor().Parse(csv, "05BB001");

        await Assert.That(readings).HasCount().EqualTo(1);
        Reading reading = readings[0];
        await Assert.That(reading.StationId).IsEqualTo("05BB001");
        await Assert.That(reading.WaterLevel).IsEqualTo(1.234);
        await Assert.That(reading.Discharge).IsEqualTo(45.6);
        await Assert.That(reading.Stamp.Offset).IsEqualTo(new TimeSpan(-6, 0, 0));
    }

    [Test]
    public async Task Parse_SkipsHeaderShortBlankAndMalformedRows_ButKeepsGoodOnes()
    {
        string csv = string.Join("\n",
            Header,
            "a,b,c",                                        // short row (< 7 cols)
            ",2024-06-01T05:00:00-06:00,1,,,,2",            // blank station
            "X,not-a-date,1,,,,2",                          // unparseable timestamp
            "05BB001,2024-06-01T05:00:00-06:00,1.5,,,,9.9"); // good

        List<Reading> readings = NewProcessor().Parse(csv, "05BB001");

        await Assert.That(readings).HasCount().EqualTo(1);
        Reading reading = readings[0];
        await Assert.That(reading.StationId).IsEqualTo("05BB001");
        await Assert.That(reading.WaterLevel).IsEqualTo(1.5);
    }

    [Test]
    public async Task Parse_EmptyDischarge_YieldsNull()
    {
        string csv = Header + "\n05BB001,2024-06-01T05:00:00-06:00,1.5,,,,";

        List<Reading> readings = NewProcessor().Parse(csv, "05BB001");
        await Assert.That(readings).HasCount().EqualTo(1);
        Reading reading = readings[0];
        await Assert.That(reading.Discharge).IsNull();
        await Assert.That(reading.WaterLevel).IsEqualTo(1.5);
    }

    [Test]
    public async Task Parse_BlankInput_ReturnsEmpty()
    {
        await Assert.That(NewProcessor().Parse("", "05BB001")).IsEmpty();
        await Assert.That(NewProcessor().Parse("   ", "05BB001")).IsEmpty();
    }
}
