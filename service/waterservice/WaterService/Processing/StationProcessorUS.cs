using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using WaterService.Data;
using WaterService.Domain;
using WaterService.Sources;

namespace WaterService.Processing;

/// <summary>
/// Coordinates fetch, parse, save, and shared exception handling for one US station at a time.
/// </summary>
public sealed class StationProcessorUS : StationProcessorBase
{
    private readonly XmlFetcherUS _fetcher;
    private readonly WaterDataRepository _dataRepo;
    private readonly ILogger<StationProcessorUS> _log;

    public StationProcessorUS(
        XmlFetcherUS fetcher,
        WaterDataRepository dataRepo,
        ILogger<StationProcessorUS> log)
    {
        _fetcher = fetcher;
        _dataRepo = dataRepo;
        _log = log;
    }

    protected override ILogger Logger => _log;
    protected override string Country => "US";
    protected override string MissingSourceDescription => "WaterML";

    protected override async Task ProcessStationAsync(string mli, string state, int tz, CancellationToken ct)
    {
        string xml = await _fetcher.FetchAsync(state, mli, ct).ConfigureAwait(false);
        List<UsSeriesReading> seriesList = Parse(xml);

        _log.LogDebug("Saving station readings. country={Country} station={Mli} state={State} series={Count}",
            Country, mli, state, seriesList.Count);
        await _dataRepo.SaveUsStationDataAsync(mli, state, seriesList, ct).ConfigureAwait(false);
        _log.LogDebug("Saved station readings. country={Country} station={Mli} state={State} series={Count}",
            Country, mli, state, seriesList.Count);
    }

    /// <summary>
    /// Parses USGS WaterML into stored-procedure payloads, one payload per variable.
    ///
    /// <para><strong>Security (XXE):</strong> WaterML is untrusted input. The reader prohibits DOCTYPE
    /// declarations and disables all external entity resolution, so a DOCTYPE/entity payload is rejected,
    /// never expanded.</para>
    /// </summary>
    internal List<UsSeriesReading> Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return new List<UsSeriesReading>();
        }

        XDocument document = LoadSecure(xml);
        var results = new List<UsSeriesReading>();

        XElement? root = document.Root;
        if (root is null || root.Name.LocalName != "timeSeriesResponse")
        {
            return results;
        }

        foreach (XElement timeSeries in root.Elements().Where(e => e.Name.LocalName == "timeSeries"))
        {
            string? fullName = timeSeries.Elements().FirstOrDefault(e => e.Name.LocalName == "variable")
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "variableName")
                ?.Value;
            if (string.IsNullOrWhiteSpace(fullName))
            {
                continue;
            }

            string[] pieces = fullName.Split(',', 2);
            string name = pieces[0].Trim();
            string? unit = pieces.Length > 1 ? NormalizeUnit(pieces[1].Trim()) : null;
            string xmlDoc = BuildLegacyXml(timeSeries);

            if (xmlDoc != "<root></root>")
            {
                results.Add(new UsSeriesReading(name, unit, xmlDoc));
            }
        }

        return results;
    }

    private static XDocument LoadSecure(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, // reject any DOCTYPE (disallow-doctype-decl)
            XmlResolver = null,                      // no external general/parameter entities, no external DTD
            MaxCharactersFromEntities = 1024,
        };

        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, settings);
        return XDocument.Load(xmlReader);
    }

    private string BuildLegacyXml(XElement timeSeries)
    {
        IEnumerable<XElement> valueNodes = timeSeries.Elements()
            .Where(e => e.Name.LocalName == "values")
            .SelectMany(v => v.Elements().Where(e => e.Name.LocalName == "value"));

        // USGS does not guarantee sample ordering, so keep the latest sample of each day by timestamp
        // rather than whichever value happens to appear last in the document.
        var samplesByDay = new Dictionary<DateTime, DailySample>();
        foreach (XElement valueNode in valueNodes)
        {
            string? dateTimeAttr = valueNode.Attributes()
                .FirstOrDefault(a => a.Name.LocalName == "dateTime")?.Value;
            if (dateTimeAttr is null)
            {
                continue;
            }

            DateTimeOffset? stamp = ParseTimestamp(dateTimeAttr);
            string? value = NormalizeNumericValue(valueNode.Value);
            if (stamp is null || value is null)
            {
                continue;
            }

            DateTime day = stamp.Value.DateTime.Date;
            if (!samplesByDay.TryGetValue(day, out DailySample existing) || stamp.Value > existing.Stamp)
            {
                samplesByDay[day] = new DailySample(stamp.Value, value);
            }
        }

        var builder = new StringBuilder("<root>");
        foreach (KeyValuePair<DateTime, DailySample> entry in samplesByDay)
        {
            builder.Append("<a d=\"")
                .Append(entry.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append("\" v=\"")
                .Append(EscapeXml(entry.Value.Value))
                .Append("\" />");
        }
        builder.Append("</root>");
        return builder.ToString();
    }

    private readonly record struct DailySample(DateTimeOffset Stamp, string Value);

    private static DateTimeOffset? ParseTimestamp(string text)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTimeOffset parsed))
        {
            return parsed;
        }
        return null;
    }

    private static string NormalizeUnit(string unit) =>
        unit.Replace("&#179;", "^3").Replace("³", "^3");

    private static string? NormalizeNumericValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? trimmed
            : null;
    }

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;")
             .Replace("\"", "&quot;")
             .Replace("<", "&lt;")
             .Replace(">", "&gt;");
}
