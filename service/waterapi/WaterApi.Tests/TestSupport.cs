using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.Registry;
using WaterApi.Configuration;
using WaterApi.Data;
using WaterApi.Domain;
using WaterApi.Services;

namespace WaterApi.Tests;

/// <summary>Scriptable repository: counts calls, returns per-country rows or throws.</summary>
internal sealed class FakeStationRepository : IStationQueryRepository
{
    private int _calls;

    public Dictionary<string, List<MapStation>> Rows { get; } = new();

    /// <summary>When set, every call throws this instead of returning rows.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Artificial latency, to let concurrent callers pile up on a cold load.</summary>
    public TimeSpan Delay { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public async Task<IReadOnlyList<MapStation>> FindMapStationsAsync(string country, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }
        if (Failure is not null)
        {
            throw Failure;
        }
        return Rows.TryGetValue(country, out List<MapStation>? rows) ? rows : [];
    }

    public static MapStation Station(int sid, string country, string state) =>
        new(sid, MapStation.ToBase36((ulong)sid), 45.0 + sid / 1e6, -80.0, country, state,
            new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc));
}

internal static class TestSupport
{
    /// <summary>A real <see cref="StationMapCache"/> over <paramref name="repository"/>, with the production
    /// SQL pipeline (fakes throw non-SQL exceptions, so nothing is retried and tests stay fast).</summary>
    public static StationMapCache NewCache(IStationQueryRepository repository, StationCacheOptions? options = null)
    {
        ServiceProvider provider = new ServiceCollection()
            .AddLogging()
            .AddWaterApiResiliencePipelines()
            .BuildServiceProvider();

        return new StationMapCache(
            repository,
            provider.GetRequiredService<ResiliencePipelineProvider<string>>(),
            Options.Create(options ?? new StationCacheOptions()),
            NullLogger<StationMapCache>.Instance,
            TimeProvider.System);
    }
}
