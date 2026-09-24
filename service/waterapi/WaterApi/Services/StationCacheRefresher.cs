using Microsoft.Extensions.Options;
using WaterApi.Configuration;

namespace WaterApi.Services;

/// <summary>
/// Keeps <see cref="StationMapCache"/> warm: loads every country at startup (when
/// <see cref="StationCacheOptions.WarmOnStartup"/>), then reloads them every
/// <see cref="StationCacheOptions.RefreshMinutes"/>. Countries refresh one after another, never in
/// parallel — the droplet is small and <c>vMapView</c> is not a cheap view.
/// </summary>
public sealed class StationCacheRefresher : BackgroundService
{
    private readonly StationMapCache _cache;
    private readonly StationCacheOptions _options;
    private readonly ILogger<StationCacheRefresher> _logger;

    public StationCacheRefresher(
        StationMapCache cache, IOptions<StationCacheOptions> options, ILogger<StationCacheRefresher> logger)
    {
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = TimeSpan.FromMinutes(Math.Max(1, _options.RefreshMinutes));
        _logger.LogInformation(
            "Station map refresher started. countries={Countries} intervalMinutes={Minutes} warmOnStartup={Warm}",
            string.Join(",", _cache.Countries), interval.TotalMinutes, _options.WarmOnStartup);

        try
        {
            if (_options.WarmOnStartup)
            {
                await RefreshAllAsync(stoppingToken).ConfigureAwait(false);
            }

            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RefreshAllAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        foreach (string country in _cache.Countries)
        {
            // RefreshAsync logs and swallows its own failures; a bad country never stops the others.
            await _cache.RefreshAsync(country, cancellationToken).ConfigureAwait(false);
        }
    }
}
