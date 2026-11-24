using System.Collections.Concurrent;
using Prometheus;

namespace EnvService.Jobs;

/// <summary>The latest outcome of one job, as <c>GET /admin/jobs</c> shows it.</summary>
public sealed record JobRun(
    string Job, DateTime? LastStartUtc, DateTime? LastEndUtc, bool? LastOk, string? LastResult, long Runs, long Failures,
    DateTime? LastSuccessUtc);

/// <summary>
/// Thread-safe registry of job outcomes (in memory: a restart starts empty) plus their Prometheus series
/// <c>env_job_runs_total{job,result}</c> and <c>env_job_last_success_timestamp_seconds{job}</c>.
/// </summary>
public sealed class JobStatus
{
    private static readonly Counter Runs = Metrics.CreateCounter(
        "env_job_runs_total", "env_service job runs by outcome.", "job", "result");
    private static readonly Gauge LastSuccess = Metrics.CreateGauge(
        "env_job_last_success_timestamp_seconds", "Unix time of the job's last successful run.", "job");

    private readonly ConcurrentDictionary<string, JobRun> _runs = new(StringComparer.OrdinalIgnoreCase);

    public void Started(string job) =>
        _runs.AddOrUpdate(job,
            j => new JobRun(j, DateTime.UtcNow, null, null, null, 0, 0, null),
            (_, r) => r with { LastStartUtc = DateTime.UtcNow });

    public void Succeeded(string job, string result)
    {
        DateTime now = DateTime.UtcNow;
        _runs.AddOrUpdate(job,
            j => new JobRun(j, now, now, true, result, 1, 0, now),
            (_, r) => r with { LastEndUtc = now, LastOk = true, LastResult = result, Runs = r.Runs + 1, LastSuccessUtc = now });
        Runs.WithLabels(job, "ok").Inc();
        LastSuccess.WithLabels(job).Set(new DateTimeOffset(now).ToUnixTimeSeconds());
    }

    public void Failed(string job, string error)
    {
        DateTime now = DateTime.UtcNow;
        _runs.AddOrUpdate(job,
            j => new JobRun(j, now, now, false, error, 1, 1, null),
            (_, r) => r with { LastEndUtc = now, LastOk = false, LastResult = error, Runs = r.Runs + 1, Failures = r.Failures + 1 });
        Runs.WithLabels(job, "failed").Inc();
    }

    public IReadOnlyList<JobRun> Snapshot() => _runs.Values.OrderBy(r => r.Job, StringComparer.Ordinal).ToList();

    public JobRun? Get(string job) => _runs.TryGetValue(job, out JobRun? r) ? r : null;
}
