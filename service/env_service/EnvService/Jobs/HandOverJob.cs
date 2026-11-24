using EnvService.Configuration;
using EnvService.Data;
using Microsoft.Extensions.Options;

namespace EnvService.Jobs;

/// <summary>
/// The hand-over from envdata to SQL Server (plan steps 4-5).
/// Every <see cref="EnvOptions.HandOverPollSeconds"/> it looks for finished ingest cycles (<c>v_job_run_pending</c>);
/// when there is one, or the last hand-over is older than <see cref="EnvOptions.HandOverMaxAgeMinutes"/>, it:
/// <list type="number">
/// <item>refreshes the flow and pressure states in envdata (<c>sp_fill_flow_state</c>, <c>sp_fill_pressure_state</c>;
/// each skips itself when younger than 6 h);</item>
/// <item>pushes <c>v_station_state</c> → <c>env.sp_push_station_state</c> and <c>v_station_day</c> →
/// <c>env.sp_push_station_day</c>, <see cref="EnvOptions.PushBatchSize"/> rows per call;</item>
/// <item>trims <c>env.StationDay</c> before today − 3 (<c>env.sp_station_day_trim</c>);</item>
/// <item>runs the fish model on the env path: <c>env.sp_push_species</c>, then <c>env.sp_update_probability</c> (plan
/// step 5). Both do nothing (-1, logged <c>probability=legacy</c>) until <c>env.Setting probability_source</c> is
/// <c>env</c>; until then the Java weather service runs <c>dbo.spTotalUpdateProbability</c>;</item>
/// <item>marks the pending cycles handed over (<c>sp_job_run_handed_over</c>) — only after every push succeeded, so a
/// failure leaves them pending and the next poll retries.</item>
/// </list>
/// The switch lives in SQL Server, so the cut-over and its rollback are one UPDATE each, with no deploy.
/// </summary>
public sealed class HandOverJob(
    IEnvStore env, IFishStore fish, JobStatus status, IOptions<EnvOptions> options, TimeProvider clock,
    ILogger<HandOverJob> log) : BackgroundService
{
    public const string Name = "handover";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime? _lastSuccessUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.HandOverEnabled)
        {
            log.LogInformation("Hand-over disabled (Env:HandOverEnabled=false).");
            return;
        }
        TimeSpan every = TimeSpan.FromSeconds(Math.Max(30, options.Value.HandOverPollSeconds));
        log.LogInformation("Hand-over started. poll={Seconds}s maxAge={Minutes}min",
            (int)every.TotalSeconds, options.Value.HandOverMaxAgeMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(force: false, stoppingToken);
            try
            {
                await Task.Delay(every, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One poll. <paramref name="force"/> = hand over even with nothing pending (<c>POST /admin/run/handover</c>).
    /// Never throws; the outcome is in <see cref="JobStatus"/> and the log. A poll with nothing to do records nothing.
    /// </summary>
    public async Task<JobRun?> RunOnceAsync(bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            IReadOnlyList<PendingJob> pending = await env.PendingJobsAsync(ct);
            DateTime now = clock.GetUtcNow().UtcDateTime;
            bool stale = _lastSuccessUtc is null
                         || now - _lastSuccessUtc.Value >= TimeSpan.FromMinutes(Math.Max(1, options.Value.HandOverMaxAgeMinutes));
            if (!force && pending.Count == 0 && !stale)
            {
                return status.Get(Name);
            }

            status.Started(Name);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            FillResult flow = await env.FillFlowAsync(ct);
            FillResult pressure = await env.FillPressureAsync(ct);

            int batch = Math.Max(1, options.Value.PushBatchSize);
            IReadOnlyList<StationStateRow> states = await env.StationStatesAsync(ct);
            int statesPushed = 0;
            foreach (StationStateRow[] chunk in states.Chunk(batch))
            {
                statesPushed += await fish.PushStationStatesAsync(chunk, ct);
            }

            int daysPushed = 0;
            var days = new List<StationDayRow>(batch);
            await foreach (StationDayRow day in env.StationDaysAsync(ct))
            {
                days.Add(day);
                if (days.Count == batch)
                {
                    daysPushed += await fish.PushStationDaysAsync(days, ct);
                    days.Clear();
                }
            }
            if (days.Count > 0)
            {
                daysPushed += await fish.PushStationDaysAsync(days, ct);
            }

            int trimmed = await fish.TrimStationDaysAsync(DateOnly.FromDateTime(now).AddDays(-3), ct);
            int species = await fish.PushSpeciesAsync(ct);
            int probability = species < 0 ? -1 : await fish.UpdateProbabilityAsync(ct);
            int marked = pending.Count == 0 ? 0 : await env.MarkHandedOverAsync(pending.Max(p => p.JobRunId), ct);

            string result = $"cycles={marked} flow={Describe(flow)} pressure={Describe(pressure)} " +
                            $"states={statesPushed} days={daysPushed} trimmed={trimmed} probability={DescribeProbability(probability)} " +
                            $"in {sw.Elapsed.TotalSeconds:F1}s";
            log.LogInformation("Hand-over done. {Result}", result);
            _lastSuccessUtc = now;
            status.Succeeded(Name, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Hand-over failed; pending cycles stay pending.");
            status.Failed(Name, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
        return status.Get(Name);
    }

    internal static string DescribeProbability(int r) => r switch
    {
        -1 => "legacy",
        -2 => "locked",
        _ => r.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    internal static string Describe(FillResult r) => r.Locked ? "locked" : r.Skipped ? "0-or-fresh" : r.Count.ToString();
}
