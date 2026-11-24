using EnvService.Configuration;
using EnvService.Data;
using Microsoft.Extensions.Options;

namespace EnvService.Jobs;

/// <summary>
/// The station mirror: the SQL Server master (<c>env.fn_station_list()</c>, ~11,700 stations incl. <c>hasFish</c>) into
/// envdata (<c>sp_station_mirror</c>), once at startup and every <see cref="EnvOptions.MirrorIntervalMinutes"/>. The full
/// list each time: <c>hasFish</c> changes without a stamp, and ~12 calls of 1,000 cost about two seconds.
/// One run at a time; <see cref="RunOnceAsync"/> is also what <c>POST /admin/run/mirror</c> calls.
/// </summary>
public sealed class StationMirrorJob(
    IFishStore fish, IEnvStore env, JobStatus status, IOptions<EnvOptions> options, ILogger<StationMirrorJob> log)
    : BackgroundService
{
    public const string Name = "mirror";
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.MirrorEnabled)
        {
            log.LogInformation("Station mirror disabled (Env:MirrorEnabled=false).");
            return;
        }
        TimeSpan every = TimeSpan.FromMinutes(Math.Max(1, options.Value.MirrorIntervalMinutes));
        log.LogInformation("Station mirror started. every={Minutes}min", (int)every.TotalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
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

    /// <summary>One mirror run; never throws (the outcome is in <see cref="JobStatus"/> and the log).</summary>
    public async Task<JobRun?> RunOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            status.Started(Name);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<StationMaster> stations = await fish.StationListAsync(ct);
            int sent = await env.MirrorStationsAsync(stations, ct);
            string result = $"stations={sent} in {sw.Elapsed.TotalSeconds:F1}s";
            log.LogInformation("Station mirror done. {Result}", result);
            status.Succeeded(Name, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Station mirror failed.");
            status.Failed(Name, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
        return status.Get(Name);
    }
}
