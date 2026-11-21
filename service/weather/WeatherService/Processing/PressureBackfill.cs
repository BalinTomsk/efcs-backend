using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WeatherService.Canonical;
using WeatherService.Configuration;
using WeatherService.Domain;
using WeatherService.Sources;

namespace WeatherService.Processing;

/// <summary>
/// Adds Open-Meteo pressure to a forecast whose provider sends none (Weather.gov). Feeds the FishFind
/// pressure forecast factor, which needs a daily pressure series for every station.
///
/// <para>Best-effort by design: disabled, out of budget, or a failed call all return the forecast
/// unchanged, so a pressure problem can never cost a station its forecast. Each call is charged
/// before the fetch under <see cref="UsageKey"/>, like every other provider.</para>
///
/// <para>Mirrors <c>com.fishfind.weather.service.PressureBackfill</c>.</para>
/// </summary>
public class PressureBackfill
{
    /// <summary>Ledger key in <see cref="WeatherApiUsageTracker"/>, separate from the CA Open-Meteo worker.</summary>
    public const string UsageKey = "OpenMeteoPressure";

    private readonly OpenMeteoFetcher _fetcher;
    private readonly WeatherApiUsageTracker _usage;
    private readonly WorkerOptions _options;
    private readonly ILogger<PressureBackfill> _log;

    public PressureBackfill(
        OpenMeteoFetcher fetcher,
        WeatherApiUsageTracker usage,
        IOptions<WorkerOptions> options,
        ILogger<PressureBackfill> log)
    {
        _fetcher = fetcher;
        _usage = usage;
        _options = options.Value;
        _log = log;
    }

    /// <summary>Returns the forecast with missing <c>pressureHpa</c> filled where Open-Meteo has a value.</summary>
    public virtual async Task<CanonicalForecast> FillAsync(
        CanonicalForecast forecast, StationRef station, CancellationToken ct = default)
    {
        if (!_options.PressureBackfill.Enabled || !PressureFill.AnyMissing(forecast))
        {
            return forecast;
        }

        if (!await _usage.TryConsumeAsync(
                UsageKey, DateOnly.FromDateTime(DateTime.Now), _options.PressureBackfill.DailyLimit, ct)
            .ConfigureAwait(false))
        {
            _log.LogDebug("Open-Meteo pressure budget spent; saving without pressure. station={Mli}", station.Mli);
            return forecast;
        }

        try
        {
            string json = await _fetcher.FetchPressureAsync(station.Latitude, station.Longitude, ct)
                .ConfigureAwait(false);
            CanonicalForecast filled = PressureFill.Fill(forecast, PressureFill.DailyPressure(json));
            _log.LogDebug("Open-Meteo pressure filled. station={Mli} days={Days}",
                station.Mli, filled.Days.Count(d => d.PressureHpa is not null));
            return filled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Open-Meteo pressure backfill failed; saving without pressure. station={Mli} error={Error}",
                station.Mli, ex.Message);
            return forecast;
        }
    }
}
