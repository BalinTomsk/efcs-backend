using Microsoft.Extensions.Logging.Abstractions;
using WaterService.Domain;
using WaterService.Processing;
using WaterService.Web;
using Xunit;

namespace WaterService.Tests;

public class StationProcessorCATests
{
    private static StationProcessorCA NewProcessor() =>
        new(fetcher: null!, dataRepo: null!, metrics: new WaterMetrics(), log: NullLogger<StationProcessorCA>.Instance);

    private const string Header = "ID,Date,Level,Grade,Symbol,QAQC,Discharge";

    [Fact]
    public void Parse_ValidRow_MapsColumns()
    {
        string csv = Header + "\n05BB001,2024-06-01T05:00:00-06:00,1.234,,,,45.6";

        List<Reading> readings = NewProcessor().Parse(csv, "05BB001");

        Reading reading = Assert.Single(readings);
        Assert.Equal("05BB001", reading.StationId);
        Assert.Equal(1.234, reading.WaterLevel);
        Assert.Equal(45.6, reading.Discharge);
        Assert.Equal(new TimeSpan(-6, 0, 0), reading.Stamp.Offset);
    }

    [Fact]
    public void Parse_SkipsHeaderShortBlankAndMalformedRows_ButKeepsGoodOnes()
    {
        string csv = string.Join("\n",
            Header,
            "a,b,c",                                        // short row (< 7 cols)
            ",2024-06-01T05:00:00-06:00,1,,,,2",            // blank station
            "X,not-a-date,1,,,,2",                          // unparseable timestamp
            "05BB001,2024-06-01T05:00:00-06:00,1.5,,,,9.9"); // good

        List<Reading> readings = NewProcessor().Parse(csv, "05BB001");

        Reading reading = Assert.Single(readings);
        Assert.Equal("05BB001", reading.StationId);
        Assert.Equal(1.5, reading.WaterLevel);
    }

    [Fact]
    public void Parse_EmptyDischarge_YieldsNull()
    {
        string csv = Header + "\n05BB001,2024-06-01T05:00:00-06:00,1.5,,,,";

        Reading reading = Assert.Single(NewProcessor().Parse(csv, "05BB001"));
        Assert.Null(reading.Discharge);
        Assert.Equal(1.5, reading.WaterLevel);
    }

    [Fact]
    public void Parse_BlankInput_ReturnsEmpty()
    {
        Assert.Empty(NewProcessor().Parse("", "05BB001"));
        Assert.Empty(NewProcessor().Parse("   ", "05BB001"));
    }
}
