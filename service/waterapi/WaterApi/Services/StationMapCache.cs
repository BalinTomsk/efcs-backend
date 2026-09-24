using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using Prometheus;
using WaterApi.Configuration;
using WaterApi.Data;
using WaterApi.Domain;

namespace WaterApi.Services;

/// <summary>
/// Process-wide cache of each country's map stations. The database is read only here — on the background
/// refresher's schedule, or once on demand when a country has never been loaded — never per HTTP request.
///
/// <para><strong>Rules:</strong></para>
/// <list type="bullet">
///   <item>A refresh builds a whole new <see cref="StationMapSnapshot"/> and swaps the reference; readers
///   always see a complete snapshot.</item>
///   <item>Loads are single-flight per country: concurrent cold requests share one query.</item>
///   <item>A failed refresh keeps the last good snapshot (logged as a warning). Only a country that has
///   <em>never</em> loaded can fail a request (<see cref="StationCacheUnavailableException"/> → 503).</item>
/// </list>
/// </summary>
public class StationMapCache
{
    private static readonly Gauge StationsGauge = Metrics.CreateGauge(
        "waterapi_station_map_stations", "Stations in the cached map snapshot.", "country");
    private static readonly Gauge LoadedGauge = Metrics.CreateGauge(
        "waterapi_station_map_loaded_timestamp_seconds", "Unix time the cached snapshot was loaded.", "country");
    private static readonly Counter RefreshCounter = Metrics.CreateCounter(
        "waterapi_station_map_refresh_total", "Station map refresh attempts by outcome.", "country", "outcome");

    private readonly IStationQueryRepository _repository;
    private readonly ResiliencePipeline _sqlPipeline;
    private readonly StationCacheOptions _options;
    private readonly ILogger<StationMapCache> _logger;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<string> _countries;

    private readonly ConcurrentDictionary<string, StationMapSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public StationMapCache(
        IStationQueryRepository repository,
        ResiliencePipelineProvider<string> pipelines,
        IOptions<StationCacheOptions> options,
        ILogger<StationMapCache> logger,
        TimeProvider time)
    {
        _repository = repository;
        _sqlPipeline = pipelines.GetPipeline(ResiliencePipelines.Sql);
        _options = options.Value;
        _logger = logger;
        _time = time;

        string[] configured = _options.Countries
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _countries = configured.Length > 0 ? configured : StationCacheOptions.DefaultCountries;
    }

    /// <summary>Countries this cache serves: upper case, distinct, in configured order.</summary>
    public IReadOnlyList<string> Countries => _countries;

    /// <summary>
    /// Returns the country's snapshot, loading it first if it has never been loaded.
    /// </summary>
    /// <exception cref="StationCacheUnavailableException">Never loaded and the load failed.</exception>
    public virtual async Task<StationMapSnapshot> GetAsync(string country, CancellationToken cancellationToken)
    {
        if (_snapshots.TryGetValue(country, out StationMapSnapshot? snapshot))
        {
            return snapshot;
        }

        if (!await RefreshAsync(country, cancellationToken).ConfigureAwait(false)
            || !_snapshots.TryGetValue(country, out snapshot))
        {
            throw new StationCacheUnavailableException(country);
        }

        return snapshot;
    }

    /// <summary>
    /// Reloads one country. Returns <c>true</c> when a snapshot is available afterwards (freshly loaded, or
    /// loaded by a concurrent caller while this one waited); <c>false</c> when the load failed.
    /// </summary>
    public virtual async Task<bool> RefreshAsync(string country, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = _locks.GetOrAdd(country, _ => new SemaphoreSlim(1, 1));
        DateTime requestedUtc = _time.GetUtcNow().UtcDateTime;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller finished a load while we waited — reuse it instead of querying again.
            if (_snapshots.TryGetValue(country, out StationMapSnapshot? current) && current.LoadedUtc >= requestedUtc)
            {
                return true;
            }

            IReadOnlyList<MapStation> stations = await _sqlPipeline
                .ExecuteAsync(async ct => await _repository.FindMapStationsAsync(country, ct).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);

            var snapshot = new StationMapSnapshot(country, stations, _time.GetUtcNow().UtcDateTime);
            _snapshots[country] = snapshot;

            StationsGauge.WithLabels(country).Set(stations.Count);
            LoadedGauge.WithLabels(country).Set(new DateTimeOffset(snapshot.LoadedUtc).ToUnixTimeSeconds());
            RefreshCounter.WithLabels(country, "success").Inc();
            _logger.LogInformation("Station map cache loaded. country={Country} stations={Count}", country, stations.Count);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RefreshCounter.WithLabels(country, "failure").Inc();
            bool haveOld = _snapshots.TryGetValue(country, out StationMapSnapshot? old);
            _logger.LogWarning(ex,
                "Station map cache refresh failed. country={Country} servingPrevious={HaveOld} previousLoadedUtc={Loaded}",
                country, haveOld, old?.LoadedUtc);
            return haveOld;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Whether a snapshot is older than <see cref="StationCacheOptions.MaxStaleMinutes"/>.</summary>
    public bool IsStale(StationMapSnapshot snapshot) =>
        _time.GetUtcNow().UtcDateTime - snapshot.LoadedUtc > TimeSpan.FromMinutes(_options.MaxStaleMinutes);
}

/// <summary>A country's map has never loaded and could not be loaded now (→ HTTP 503).</summary>
public sealed class StationCacheUnavailableException(string country)
    : Exception($"Station map for {country} is not available yet");
