using EnvService.Configuration;
using EnvService.Data;
using EnvService.Jobs;
using Microsoft.Extensions.Options;

namespace EnvService.Reads;

/// <summary>
/// Every station of envdata (<c>v_station_info</c>, ~12,000) in memory, by sid and by water body, so no request scans
/// the station table. Refreshed at startup and every <see cref="EnvOptions.StationCacheMinutes"/>. A failed refresh keeps
/// the last good snapshot; until the first load succeeds <see cref="Current"/> is null and the API answers 503.
/// </summary>
public sealed class StationCache
{
    private volatile Snapshot? _snapshot;

    public Snapshot? Current => _snapshot;

    /// <summary>Replaces the snapshot (the cache job, and tests).</summary>
    public void Load(IReadOnlyList<StationInfo> stations, DateTime loadedUtc) => _snapshot = new Snapshot(stations, loadedUtc);

    /// <summary>One immutable load of the station list.</summary>
    public sealed class Snapshot
    {
        public Snapshot(IReadOnlyList<StationInfo> stations, DateTime loadedUtc)
        {
            LoadedUtc = loadedUtc;
            Count = stations.Count;
            BySid = stations.ToDictionary(s => s.Sid);
            ByLake = stations
                .Where(s => s.LakeId is not null)
                .GroupBy(s => s.LakeId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key.ToLowerInvariant(),
                    g => (IReadOnlyList<StationInfo>)g.OrderBy(s => s.Mli, StringComparer.Ordinal).ToList(),
                    StringComparer.OrdinalIgnoreCase);
            MapByCountry = stations
                .Where(OnMap)
                .GroupBy(s => s.Country.Trim().ToUpperInvariant(), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<StationInfo>)g.OrderBy(s => s.Sid).ToList(), StringComparer.Ordinal);
        }

        /// <summary>The countries the station map serves (waterapi's set).</summary>
        public static readonly string[] MapCountries = ["CA", "US"];

        /// <summary>
        /// On the station map, as SQL Server's <c>dbo.vMapView</c> chose (waterapi, plan step 6c): the station has reported
        /// at least once, is in a map country, and is not in Hawaii or Puerto Rico.
        /// </summary>
        public static bool OnMap(StationInfo s) =>
            s.UpdData is not null
            && MapCountries.Contains(s.Country.Trim().ToUpperInvariant(), StringComparer.Ordinal)
            && s.State?.Trim().ToUpperInvariant() is not ("HI" or "PR");

        /// <summary>The map pins by country (key upper case), ordered by sid.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<StationInfo>> MapByCountry { get; }

        public DateTime LoadedUtc { get; }

        public int Count { get; }

        public IReadOnlyDictionary<int, StationInfo> BySid { get; }

        /// <summary>Key = lower-case lake GUID; stations ordered by mli (as the website's Weather tab lists them).</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<StationInfo>> ByLake { get; }
    }
}

/// <summary>Keeps <see cref="StationCache"/> fresh; its outcome shows in <c>GET /admin/jobs</c> as <c>stations</c>.</summary>
public sealed class StationCacheJob(
    IEnvReadStore store, StationCache cache, JobStatus status, TimeProvider clock, IOptions<EnvOptions> options,
    ILogger<StationCacheJob> log) : BackgroundService
{
    public const string Name = "stations";
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan every = TimeSpan.FromMinutes(Math.Max(1, options.Value.StationCacheMinutes));
        log.LogInformation("Station cache started. every={Minutes}min", (int)every.TotalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            // Until the first load succeeds, retry every 30 s: the API answers 503 meanwhile.
            TimeSpan wait = cache.Current is null ? TimeSpan.FromSeconds(30) : every;
            try
            {
                await Task.Delay(wait, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One refresh; never throws (the outcome is in <see cref="JobStatus"/> and the log).</summary>
    public async Task<JobRun?> RunOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            status.Started(Name);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<StationInfo> stations = await store.StationInfosAsync(ct);
            cache.Load(stations, clock.GetUtcNow().UtcDateTime);
            string result = $"stations={stations.Count} in {sw.Elapsed.TotalSeconds:F1}s";
            log.LogInformation("Station cache done. {Result}", result);
            status.Succeeded(Name, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Station cache refresh failed; the last snapshot stays.");
            status.Failed(Name, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
        return status.Get(Name);
    }
}
