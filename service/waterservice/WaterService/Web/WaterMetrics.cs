using Prometheus;
using WaterService.Processing;

namespace WaterService.Web;

/// <summary>
/// Custom operational metrics, the prometheus-net equivalent of the Java Micrometer counters.
/// </summary>
public class WaterMetrics
{
    private readonly Counter _stationProcessed = Metrics.CreateCounter(
        "water_station_processed_total",
        "Stations processed per cycle.",
        new CounterConfiguration { LabelNames = new[] { "country", "outcome" } });

    private readonly Counter _csvRowsSkipped = Metrics.CreateCounter(
        "water_csv_rows_skipped_total",
        "Malformed CSV rows skipped while parsing.",
        new CounterConfiguration { LabelNames = new[] { "country" } });

    private readonly Counter _cycleOverrun = Metrics.CreateCounter(
        "water_cycle_overrun_total",
        "Cycles that overran their cron period (the next scheduled trigger was skipped).");

    /// <summary>
    /// Records the outcome of one station for a country. The <c>outcome</c> label distinguishes a
    /// <em>skip</em> (the upstream feed publishes nothing for that station — routine, not an error) from a
    /// real failure; collapsing both into <c>failure</c> made a healthy cycle look like an outage and
    /// buried genuine failures in the noise.
    /// </summary>
    public virtual void StationProcessed(string country, ProcessingOutcome outcome) =>
        _stationProcessed.WithLabels(country, OutcomeLabel(outcome)).Inc();

    /// <summary>Maps a <see cref="ProcessingOutcome"/> to its stable Prometheus label value.</summary>
    internal static string OutcomeLabel(ProcessingOutcome outcome) =>
        outcome switch
        {
            ProcessingOutcome.Processed => "success",
            ProcessingOutcome.Skipped => "skipped",
            ProcessingOutcome.FailedHttp503 => "failure_503",
            ProcessingOutcome.FailedUpstreamOpen => "upstream_open",
            _ => "failure",
        };

    /// <summary>Records skipped malformed CSV rows for a country.</summary>
    public virtual void CsvRowsSkipped(string country, int count) =>
        _csvRowsSkipped.WithLabels(country).Inc(count);

    /// <summary>Records a cycle that overran its cron period.</summary>
    public virtual void CycleOverrun() => _cycleOverrun.Inc();
}
