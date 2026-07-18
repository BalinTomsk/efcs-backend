using Prometheus;

namespace WaterService.Web;

/// <summary>
/// Custom operational metrics, the prometheus-net equivalent of the Java Micrometer counters.
/// </summary>
public sealed class WaterMetrics
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

    /// <summary>Records a processed station outcome (success / failure) for a country.</summary>
    public void StationProcessed(string country, bool success) =>
        _stationProcessed.WithLabels(country, success ? "success" : "failure").Inc();

    /// <summary>Records skipped malformed CSV rows for a country.</summary>
    public void CsvRowsSkipped(string country, int count) =>
        _csvRowsSkipped.WithLabels(country).Inc(count);

    /// <summary>Records a cycle that overran its cron period.</summary>
    public void CycleOverrun() => _cycleOverrun.Inc();
}
