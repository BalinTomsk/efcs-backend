using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using WaterService.Data;
using WaterService.Domain;
using WaterService.Sources;
using WaterService.Web;

namespace WaterService.Processing;

/// <summary>
/// Coordinates fetch, parse, save, and shared exception handling for one CA station at a time.
/// </summary>
public sealed class StationProcessorCA : StationProcessorBase
{
    private const int MinColumns = 7;

    private readonly CsvFetcherCA _fetcher;
    private readonly WaterDataRepository _dataRepo;
    private readonly WaterMetrics _metrics;
    private readonly ILogger<StationProcessorCA> _log;

    public StationProcessorCA(
        CsvFetcherCA fetcher,
        WaterDataRepository dataRepo,
        WaterMetrics metrics,
        ILogger<StationProcessorCA> log)
    {
        _fetcher = fetcher;
        _dataRepo = dataRepo;
        _metrics = metrics;
        _log = log;
    }

    protected override ILogger Logger => _log;
    protected override string Country => "CA";
    protected override string MissingSourceDescription => "hydrometric CSV";

    protected override async Task ProcessStationAsync(string mli, string state, int tz, CancellationToken ct)
    {
        string csv = await _fetcher.FetchAsync(state, mli, ct).ConfigureAwait(false);
        List<Reading> readings = Parse(csv, mli);

        _log.LogDebug("Saving station readings. country={Country} station={Mli} state={State} readings={Count}",
            Country, mli, state, readings.Count);
        await _dataRepo.SaveStationDataAsync(mli, readings, ct).ConfigureAwait(false);
        _log.LogDebug("Saved station readings. country={Country} station={Mli} state={State} readings={Count}",
            Country, mli, state, readings.Count);
    }

    /// <summary>
    /// Parses the downloaded CSV into readings. Parsing is fault-tolerant: the header row, short rows,
    /// rows missing the station id/timestamp, and rows that fail to parse are skipped rather than aborting
    /// the whole station's batch. Skipped rows are counted on the <c>water_csv_rows_skipped_total</c> metric.
    /// </summary>
    internal List<Reading> Parse(string csv, string mli)
    {
        var list = new List<Reading>();
        if (string.IsNullOrWhiteSpace(csv))
        {
            return list;
        }

        int skipped = 0;
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            TrimOptions = TrimOptions.Trim,
            BadDataFound = null,
            MissingFieldFound = null,
            DetectColumnCountChanges = false,
        };

        using var reader = new StringReader(csv);
        using var parser = new CsvParser(reader, config);

        bool first = true;
        while (parser.Read())
        {
            if (first)
            {
                first = false; // skip header
                continue;
            }

            if (parser.Count < MinColumns)
            {
                skipped++;
                continue;
            }

            try
            {
                string stationId = parser[0];
                string stampText = parser[1];
                if (string.IsNullOrEmpty(stationId) || string.IsNullOrEmpty(stampText))
                {
                    skipped++;
                    continue;
                }

                if (!DateTimeOffset.TryParse(stampText, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTimeOffset stamp))
                {
                    skipped++;
                    continue;
                }

                double? waterLevel = ParseDouble(parser[2]);
                double? discharge = ParseDouble(parser[6]);

                list.Add(new Reading(stationId, stamp, waterLevel, discharge));
            }
            catch (Exception ex)
            {
                // A single malformed row must not discard the rest of the station's readings.
                skipped++;
                _log.LogDebug(ex, "Skipping malformed CSV row. station={Mli} row={Row}", mli, parser.Row);
            }
        }

        if (skipped > 0)
        {
            _metrics.CsvRowsSkipped(Country, skipped);
            _log.LogInformation("Skipped malformed CSV rows. station={Mli} skipped={Skipped}", mli, skipped);
        }

        return list;
    }

    private static double? ParseDouble(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        return double.Parse(s.Trim(), CultureInfo.InvariantCulture);
    }
}
