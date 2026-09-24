using System.Data.Common;
using Microsoft.Data.SqlClient;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace WaterApi.Configuration;

/// <summary>
/// The named Polly pipeline around every database read — the <c>sqlRetry</c>/<c>sqlBreaker</c> pair of
/// docapi and water-station-pusher, same settings.
///
/// <para>The circuit breaker is added first, so it is the <em>outermost</em> strategy: an open breaker
/// short-circuits immediately instead of being retried, and all retries of one call count as a single
/// breaker outcome. Polly's own per-retry telemetry is silenced in <c>Program.cs</c>; one line is logged
/// per breaker state change instead.</para>
/// </summary>
public static class ResiliencePipelines
{
    public const string Sql = "sql";

    public static IServiceCollection AddWaterApiResiliencePipelines(this IServiceCollection services)
    {
        services.AddResiliencePipeline(Sql, (builder, context) =>
        {
            ILogger logger = context.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger("WaterApi.Resilience." + Sql);
            PredicateBuilder<object> handle = new PredicateBuilder().Handle<Exception>(IsSqlFailure);

            builder
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = 5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    BreakDuration = TimeSpan.FromSeconds(30),
                    ShouldHandle = handle,
                    OnOpened = args =>
                    {
                        logger.LogWarning("Circuit breaker '{Breaker}' opened for {BreakSeconds}s after sustained failures.",
                            Sql, (int)args.BreakDuration.TotalSeconds);
                        return default;
                    },
                    OnClosed = _ =>
                    {
                        logger.LogInformation("Circuit breaker '{Breaker}' closed (recovered).", Sql);
                        return default;
                    },
                    OnHalfOpened = _ =>
                    {
                        logger.LogInformation("Circuit breaker '{Breaker}' half-opened (probing).", Sql);
                        return default;
                    },
                })
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 2, // 1 initial + 2 retries = 3 attempts
                    Delay = TimeSpan.FromSeconds(2),
                    BackoffType = DelayBackoffType.Constant,
                    ShouldHandle = handle,
                });
        });

        return services;
    }

    /// <summary>Transient SQL failures worth retrying / tripping the breaker.</summary>
    private static bool IsSqlFailure(Exception ex) =>
        ex is SqlException or DbException or TimeoutException;
}
