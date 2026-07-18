using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace WaterService.Configuration;

/// <summary>
/// Registers the named Polly resilience pipelines that mirror the Java Resilience4j configuration.
///
/// <para><strong>Ordering:</strong> in every pipeline the circuit breaker is added first, so it is the
/// <em>outermost</em> strategy wrapping the retry — an open breaker short-circuits immediately instead of
/// being retried, and all retries of one call count as a single breaker outcome.</para>
///
/// <para><strong>Logging:</strong> Polly's own execution/retry telemetry is intentionally silenced at the
/// logging level (see <c>Program.cs</c>) because it logs a full stack trace per <em>handled</em> retry —
/// which for tens of thousands of stations is noise. Instead we log a single concise line only on a
/// circuit-breaker <em>state change</em> (opened / closed), and the per-station processors log one line
/// per station that ultimately fails.</para>
/// </summary>
public static class ResiliencePipelines
{
    public const string Sql = "sql";
    public const string CaFeed = "caFeed";
    public const string UsFeed = "usFeed";

    public static IServiceCollection AddWaterResiliencePipelines(this IServiceCollection services)
    {
        // SQL: retry sqlRetry (3 attempts / 2s) + breaker sqlBreaker.
        services.AddResiliencePipeline(Sql, (builder, context) =>
        {
            ILogger logger = BreakerLogger(context.ServiceProvider, Sql);
            builder
                .AddCircuitBreaker(BreakerOptions(0.5, 5, IsSqlFailure, logger, Sql))
                .AddRetry(RetryOptions(IsSqlFailure));
        });

        // Per-feed HTTP pipelines: shared httpRetry inside a separate breaker each, so one feed's outage
        // does not trip the other. FileNotFoundException (HTTP 404 => source not published) is neither
        // retried nor counted against the breaker.
        AddFeedPipeline(services, CaFeed);
        AddFeedPipeline(services, UsFeed);

        return services;
    }

    private static void AddFeedPipeline(IServiceCollection services, string name)
    {
        services.AddResiliencePipeline(name, (builder, context) =>
        {
            ILogger logger = BreakerLogger(context.ServiceProvider, name);
            builder
                .AddCircuitBreaker(BreakerOptions(0.5, 10, IsHttpTransient, logger, name))
                .AddRetry(RetryOptions(IsHttpTransient));
        });
    }

    private static CircuitBreakerStrategyOptions BreakerOptions(
        double failureRatio, int minimumThroughput, Func<Exception, bool> shouldHandle, ILogger logger, string name) =>
        new()
        {
            FailureRatio = failureRatio,
            MinimumThroughput = minimumThroughput,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = new PredicateBuilder().Handle<Exception>(shouldHandle),
            // Concise, one-line-per-state-change breaker logging (replaces Polly's verbose telemetry).
            OnOpened = args =>
            {
                logger.LogWarning("Circuit breaker '{Breaker}' opened for {BreakSeconds}s after sustained failures.",
                    name, (int)args.BreakDuration.TotalSeconds);
                return default;
            },
            OnClosed = _ =>
            {
                logger.LogInformation("Circuit breaker '{Breaker}' closed (recovered).", name);
                return default;
            },
            OnHalfOpened = _ =>
            {
                logger.LogInformation("Circuit breaker '{Breaker}' half-opened (probing).", name);
                return default;
            },
        };

    private static RetryStrategyOptions RetryOptions(Func<Exception, bool> shouldHandle) =>
        new()
        {
            MaxRetryAttempts = 2, // 1 initial + 2 retries = 3 attempts
            Delay = TimeSpan.FromSeconds(2),
            BackoffType = DelayBackoffType.Constant,
            ShouldHandle = new PredicateBuilder().Handle<Exception>(shouldHandle),
        };

    private static ILogger BreakerLogger(IServiceProvider serviceProvider, string name) =>
        serviceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("WaterService.Resilience." + name);

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
            // HttpClient surfaces a request/connect timeout as a cancellation with a TimeoutException inner.
            TaskCanceledException tce => tce.InnerException is TimeoutException,
            _ => false,
        };
    }
}
