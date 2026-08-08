using WeatherService.Configuration;

namespace WeatherService.Sources;

/// <summary>
/// The per-provider <see cref="ProviderRateLimiter"/> instances, keyed by pipeline name — the
/// counterpart of the <c>resilience4j.ratelimiter.instances.*</c> block in <c>application.yml</c>.
///
/// <para>Values are fixed in code rather than configurable because they encode each provider's
/// published request ceiling, not a tuning knob: Open-Meteo tolerates a short burst, the government
/// feeds want roughly one request per second, and Environment Canada's GeoMet is slower still.</para>
/// </summary>
public sealed class ProviderRateLimiters : IDisposable
{
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, ProviderRateLimiter> _limiters;

    public ProviderRateLimiters()
    {
        _limiters = new Dictionary<string, ProviderRateLimiter>(StringComparer.Ordinal)
        {
            [ResiliencePipelines.OpenMeteo] = Limiter(ResiliencePipelines.OpenMeteo, 5, TimeSpan.FromSeconds(1)),
            [ResiliencePipelines.WeatherGov] = Limiter(ResiliencePipelines.WeatherGov, 1, TimeSpan.FromSeconds(1)),
            [ResiliencePipelines.VisualCrossing] = Limiter(ResiliencePipelines.VisualCrossing, 1, TimeSpan.FromSeconds(1)),
            [ResiliencePipelines.GoogleWeather] = Limiter(ResiliencePipelines.GoogleWeather, 1, TimeSpan.FromSeconds(1)),
            [ResiliencePipelines.WeatherCanada] = Limiter(ResiliencePipelines.WeatherCanada, 1, TimeSpan.FromSeconds(5)),
        };
    }

    /// <summary>Returns the limiter for a pipeline name.</summary>
    /// <exception cref="KeyNotFoundException">No limiter is registered under that name.</exception>
    public ProviderRateLimiter Get(string pipelineName) => _limiters[pipelineName];

    public void Dispose()
    {
        foreach (ProviderRateLimiter limiter in _limiters.Values)
        {
            limiter.Dispose();
        }
    }

    private static ProviderRateLimiter Limiter(string name, int permits, TimeSpan window) =>
        new(name, permits, window, AcquireTimeout);
}
