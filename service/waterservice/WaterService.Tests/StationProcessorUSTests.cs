using System.Xml;
using Microsoft.Extensions.Logging.Abstractions;
using WaterService.Domain;
using WaterService.Processing;
using Xunit;

namespace WaterService.Tests;

public class StationProcessorUSTests
{
    private static StationProcessorUS NewProcessor() =>
        new(fetcher: null!, dataRepo: null!, log: NullLogger<StationProcessorUS>.Instance);

    [Fact]
    public void Parse_KeepsLatestSamplePerDay_AndNormalizesUnit()
    {
        // variableName uses the &#179; numeric entity for the cubed sign, like real USGS payloads.
        string xml =
            "<timeSeriesResponse xmlns=\"http://www.cuahsi.org/waterML/1.1/\">" +
            "  <timeSeries>" +
            "    <variable><variableName>Streamflow, ft&#179;/s</variableName></variable>" +
            "    <values>" +
            "      <value dateTime=\"2024-06-01T00:00:00.000-05:00\">100</value>" +
            "      <value dateTime=\"2024-06-01T12:00:00.000-05:00\">150</value>" +
            "      <value dateTime=\"2024-06-02T06:00:00.000-05:00\">200</value>" +
            "    </values>" +
            "  </timeSeries>" +
            "</timeSeriesResponse>";

        List<UsSeriesReading> series = NewProcessor().Parse(xml);

        UsSeriesReading reading = Assert.Single(series);
        Assert.Equal("Streamflow", reading.Name);
        Assert.Equal("ft^3/s", reading.Unit);
        Assert.Equal(
            "<root><a d=\"2024-06-01\" v=\"150\" /><a d=\"2024-06-02\" v=\"200\" /></root>",
            reading.XmlDoc);
    }

    [Fact]
    public void Parse_BlankInput_ReturnsEmpty()
    {
        Assert.Empty(NewProcessor().Parse(""));
        Assert.Empty(NewProcessor().Parse("   "));
    }

    [Fact]
    public void Parse_NonNumericValues_AreSkipped()
    {
        string xml =
            "<timeSeriesResponse xmlns=\"http://www.cuahsi.org/waterML/1.1/\">" +
            "  <timeSeries>" +
            "    <variable><variableName>Gage height, ft</variableName></variable>" +
            "    <values>" +
            "      <value dateTime=\"2024-06-01T00:00:00.000-05:00\">-999999</value>" +
            "      <value dateTime=\"2024-06-01T06:00:00.000-05:00\">Ice</value>" +
            "    </values>" +
            "  </timeSeries>" +
            "</timeSeriesResponse>";

        UsSeriesReading reading = Assert.Single(NewProcessor().Parse(xml));
        Assert.Equal("Gage height", reading.Name);
        Assert.Equal("ft", reading.Unit);
        // "Ice" is non-numeric and dropped; the numeric -999999 is the latest valid sample for the day.
        Assert.Equal("<root><a d=\"2024-06-01\" v=\"-999999\" /></root>", reading.XmlDoc);
    }

    [Fact]
    public void Parse_RejectsDoctype_XxeHardened()
    {
        string xml =
            "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>" +
            "<timeSeriesResponse><timeSeries></timeSeries></timeSeriesResponse>";

        Assert.Throws<XmlException>(() => NewProcessor().Parse(xml));
    }
}
