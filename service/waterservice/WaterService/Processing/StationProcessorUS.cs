using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using WaterService.Data;
using WaterService.Domain;
using WaterService.Sources;

namespace WaterService.Processing;

/// <summary>
/// Coordinates save and shared exception handling for one US station at a time, reading from a
/// per-cycle snapshot of the USGS Water Data API instead of calling USGS once per station.
///
/// <para>The worker calls <see cref="PrepareCycleAsync"/> once at the start of each US pass; each station
/// is then a lookup in that snapshot. A station with no current series is skipped, as a 404 used to be.</para>
/// </summary>
public class StationProcessorUS : StationProcessorBase
{
    /// <summary>USGS statistic code for an instantaneous value — what the legacy <c>nwis/iv</c> feed served.</summary>
    private const string InstantaneousStatistic = "00011";

    private readonly UsgsWaterDataClient _client;
    private readonly WaterDataRepository _dataRepo;
    private readonly ILogger<StationProcessorUS> _log;

    private IReadOnlyDictionary<string, List<UsgsObservation>>? _snapshot;

    public StationProcessorUS(
        UsgsWaterDataClient client,
        WaterDataRepository dataRepo,
        ILogger<StationProcessorUS> log)
    {
        _client = client;
        _dataRepo = dataRepo;
        _log = log;
    }

    protected override ILogger Logger => _log;
    protected override string Country => "US";
    protected override string MissingSourceDescription => "USGS series";

    /// <summary>
    /// Loads this cycle's USGS snapshot and the time zones of the stations found in it. Throws when the
    /// snapshot cannot be fetched, which fails the whole US pass.
    /// </summary>
    /// <param name="mlis">The supported US stations for this pass.</param>
    /// <param name="requestedMli">A single requested station, or <c>null</c> for the full snapshot.</param>
    public virtual async Task PrepareCycleAsync(IReadOnlyCollection<string> mlis, string? requestedMli, CancellationToken ct)
    {
        IReadOnlyDictionary<string, List<UsgsObservation>> snapshot =
            await _client.FetchLatestAsync(requestedMli, ct).ConfigureAwait(false);
        await _client.EnsureTimeZonesAsync(mlis.Where(snapshot.ContainsKey), ct).ConfigureAwait(false);
        _snapshot = snapshot;
    }

    protected override async Task ProcessStationAsync(string mli, string state, int tz, CancellationToken ct)
    {
        if (_snapshot is null || !_snapshot.TryGetValue(mli, out List<UsgsObservation>? observations))
        {
            throw new FileNotFoundException(
                $"No current USGS series for US station {mli} (state {state})");
        }

        List<UsSeriesReading> seriesList = BuildReadings(observations, _client.TimeZoneFor(mli));

        _log.LogDebug("Saving station readings. country={Country} station={Mli} state={State} series={Count}",
            Country, mli, state, seriesList.Count);
        await _dataRepo.SaveUsStationDataAsync(mli, state, seriesList, ct).ConfigureAwait(false);
        _log.LogDebug("Saved station readings. country={Country} station={Mli} state={State} series={Count}",
            Country, mli, state, seriesList.Count);
    }

    /// <summary>
    /// Turns a station's latest USGS values into stored-procedure payloads, one per series, named and
    /// united as the legacy WaterML feed named them (<see cref="UsgsParameterMap"/>) and dated by the
    /// station's local day. Unmapped parameters, non-instantaneous statistics and non-numeric values are
    /// dropped.
    /// </summary>
    internal static List<UsSeriesReading> BuildReadings(IEnumerable<UsgsObservation> observations, TimeZoneInfo zone)
    {
        var results = new List<UsSeriesReading>();
        foreach (UsgsObservation observation in observations)
        {
            if (observation.StatisticId is not null && observation.StatisticId != InstantaneousStatistic)
            {
                continue;
            }

            UsgsParameterMap.LegacyName? legacy = UsgsParameterMap.Find(observation.ParameterCode);
            string? value = NormalizeNumericValue(observation.Value);
            if (legacy is null || value is null)
            {
                continue;
            }

            DateTime day = TimeZoneInfo.ConvertTime(observation.Time, zone).Date;
            string xmlDoc = new StringBuilder("<root><a d=\"")
                .Append(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append("\" v=\"")
                .Append(EscapeXml(value))
                .Append("\" /></root>")
                .ToString();
            results.Add(new UsSeriesReading(legacy.Name, legacy.Unit, xmlDoc));
        }

        return results;
    }

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
