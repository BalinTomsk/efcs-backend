using System.Data.Common;
using Microsoft.Data.SqlClient;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace WaterService.Configuration;

/// <summary>
/// Registers the named Polly resilience pipelines that mirror the Java Resilience4j configuration.
///
/// <para><strong>Ordering:</strong> in every pipeline the circuit breaker is added first, so it is the
/// <em>outermost</em> strategy wrapping the retry — an open breaker short-circuits immediately instead of
/// being retried, and all retries of one call count as a single breaker outcome. This matches the Java
/// <c>circuit-breaker-aspect-order (2) &gt; retry-aspect-order (1)</c>.</para>
/// </summary>
public static class ResiliencePipelines
{
    public const string Sql = "sql";
    public const string CaFeed = "caFeed";
    public const string UsFeed = "usFeed";

    public static IServiceCollection AddWaterResiliencePipelines(this IServiceCollection services)
    {
        // SQL: retry sqlRetry (3 attempts / 2s) + breaker sqlBreaker.
        services.AddResiliencePipeline(Sql, builder => builder
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsSqlFailure),
            })
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2, // 1 initial + 2 retries = 3 attempts
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsSqlFailure),
            }));

        // Per-feed HTTP pipelines: shared httpRetry inside a separate breaker each, so one feed's outage
        // does not trip the other. FileNotFoundException (HTTP 404 => source not published) is neither
        // retried nor counted against the breaker.
        AddFeedPipeline(services, CaFeed);
        AddFeedPipeline(services, UsFeed);

        return services;
    }

    private static void AddFeedPipeline(IServiceCollection services, string name)
    {
        services.AddResiliencePipeline(name, builder => builder
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsHttpTransient),
            })
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2, // 1 initial + 2 retries = 3 attempts
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsHttpTransient),
            }));
    }

    /// <summary>Transient SQL failures worth retrying / tripping the breaker.</summary>
    private static bool IsSqlFailure(Exception ex) =>
        ex is SqlException or DbException or TimeoutException;

    /// <summary>
    /// Transient HTTP failures (network errors, premature EOF, socket/read timeouts, 5xx). A 404 is
    /// surfaced as <see cref="FileNotFoundException"/> and is deliberately NOT handled here.
    /// </summary>
    private static bool IsHttpTransient(Exception ex)
    {
        if (ex is FileNotFoundException)
        {
            return false;
        }

        return ex switch
        {
            HttpRequestException => true,
            TimeoutException => true,
            // HttpClient surfaces a request timeout as a cancellation with a TimeoutException inner.
            TaskCanceledException tce => tce.InnerException is TimeoutException,
            _ => false,
        };
    }
}
