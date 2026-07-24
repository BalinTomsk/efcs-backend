using System.Xml;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using WaterService.Domain;
using WaterService.Processing;

namespace WaterService.Tests;

public class StationProcessorUSTests
{
    private static StationProcessorUS NewProcessor() =>
        new(fetcher: null!, dataRepo: null!, log: NullLogger<StationProcessorUS>.Instance);

    [Test]
    public async Task Parse_KeepsLatestSamplePerDay_AndNormalizesUnit()
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

        await Assert.That(series).HasCount().EqualTo(1);
        UsSeriesReading reading = series[0];
        await Assert.That(reading.Name).IsEqualTo("Streamflow");
        await Assert.That(reading.Unit).IsEqualTo("ft^3/s");
        await Assert.That(reading.XmlDoc)
            .IsEqualTo("<root><a d=\"2024-06-01\" v=\"150\" /><a d=\"2024-06-02\" v=\"200\" /></root>");
    }

    [Test]
    public async Task Parse_BlankInput_ReturnsEmpty()
    {
        await Assert.That(NewProcessor().Parse("")).IsEmpty();
        await Assert.That(NewProcessor().Parse("   ")).IsEmpty();
    }

    [Test]
    public async Task Parse_NonNumericValues_AreSkipped()
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

        List<UsSeriesReading> series = NewProcessor().Parse(xml);
        await Assert.That(series).HasCount().EqualTo(1);
        UsSeriesReading reading = series[0];
        await Assert.That(reading.Name).IsEqualTo("Gage height");
        await Assert.That(reading.Unit).IsEqualTo("ft");
        // "Ice" is non-numeric and dropped; the numeric -999999 is the latest valid sample for the day.
        await Assert.That(reading.XmlDoc).IsEqualTo("<root><a d=\"2024-06-01\" v=\"-999999\" /></root>");
    }

    [Test]
    public async Task Parse_RejectsDoctype_XxeHardened()
    {
        string xml =
            "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>" +
            "<timeSeriesResponse><timeSeries></timeSeries></timeSeriesResponse>";

        await Assert.That(() => NewProcessor().Parse(xml))
            .Throws<XmlException>();
    }
}
