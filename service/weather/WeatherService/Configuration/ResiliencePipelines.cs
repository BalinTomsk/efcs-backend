using System.Data.Common;
using Microsoft.Data.SqlClient;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using WeatherService.Sources;

namespace WeatherService.Configuration;

/// <summary>
/// Registers the named Polly resilience pipelines that mirror the Java Resilience4j configuration.
///
/// <para><strong>Ordering:</strong> retry is added first and is therefore the <em>outermost</em>
/// strategy, wrapping the circuit breaker — the same nesting Resilience4j's Spring annotations use
/// (Retry &gt; CircuitBreaker &gt; RateLimiter). An open breaker is still not retried, because
/// <see cref="BrokenCircuitException"/> is excluded from the retry predicate, exactly as
/// <c>CallNotPermittedException</c> sits in each instance's <c>ignore-exceptions</c> list in
/// <c>application.yml</c>.</para>
///
/// <para>The third Resilience4j layer, the per-provider rate limiter, is not a Polly strategy here:
/// it is applied as the innermost step inside the fetch delegate (see <see cref="ProviderRateLimiter"/>),
/// which preserves the same nesting while keeping Resilience4j's "wait up to <c>timeout-duration</c>
/// for a permit, then fail" semantics that Polly's rate-limiter strategy does not express.</para>
///
/// <para><strong>Logging:</strong> Polly's own execution/retry telemetry is silenced at the logging
/// level (see <c>Program.cs</c>) because it logs a full stack trace per <em>handled</em> retry — noise
/// across thousands of stations. Instead one concise line is logged on a breaker state change, and the
/// station processors log one line per station that ultimately fails.</para>
/// </summary>
public static class ResiliencePipelines
{
    public const string Sql = "sql";
    public const string OpenMeteo = "openMeteo";
    public const string WeatherGov = "weatherGov";
    public const string VisualCrossing = "visualCrossing";
    public const string GoogleWeather = "googleWeather";
    public const string WeatherCanada = "weatherCanada";
    public const string Wunderground = "wunderground";

    /// <summary>Every upstream provider pipeline, in registration order.</summary>
    public static readonly string[] FeedPipelines =
        [OpenMeteo, WeatherGov, VisualCrossing, GoogleWeather, WeatherCanada, Wunderground];

    public static IServiceCollection AddWeatherResiliencePipelines(this IServiceCollection services)
    {
        // SQL: retry sqlRetry (3 attempts / 2s constant) around breaker sqlBreaker.
        services.AddResiliencePipeline(Sql, (builder, context) =>
        {
            ILogger logger = BreakerLogger(context.ServiceProvider, Sql);
            builder
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 2, // 1 initial + 2 retries = max-attempts: 3
                    Delay = TimeSpan.FromSeconds(2),
                    BackoffType = DelayBackoffType.Constant,
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(IsSqlFailure),
                })
                .AddCircuitBreaker(BreakerOptions(
                    minimumThroughput: 5, breakSeconds: 30, shouldHandle: IsSqlFailure, logger: logger, name: Sql));
        });

        // One pipeline per upstream provider, so one provider's outage cannot trip another's breaker.
        foreach (string name in FeedPipelines)
        {
            AddFeedPipeline(services, name);
        }

        return services;
    }

    private static void AddFeedPipeline(IServiceCollection services, string name)
    {
        services.AddResiliencePipeline(name, (builder, context) =>
        {
            ILogger logger = BreakerLogger(context.ServiceProvider, name);
            builder
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3, // 1 initial + 3 retries = max-attempts: 4
                    Delay = TimeSpan.FromSeconds(1),
                    BackoffType = DelayBackoffType.Exponential, // exponential-backoff-multiplier: 2
                    UseJitter = true,                           // randomized-wait-factor: 0.5
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(IsFeedRetryable),
                })
                .AddCircuitBreaker(BreakerOptions(
                    minimumThroughput: 10, breakSeconds: 60, shouldHandle: IsFeedRecordable, logger: logger, name: name));
        });
    }

    private static CircuitBreakerStrategyOptions BreakerOptions(
        int minimumThroughput, int breakSeconds, Func<Exception, bool> shouldHandle, ILogger logger, string name) =>
        new()
        {
            FailureRatio = 0.5,                                       // failure-rate-threshold: 50
            MinimumThroughput = minimumThroughput,                    // minimum-number-of-calls
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(breakSeconds),       // wait-duration-in-open-state
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

    private static ILogger BreakerLogger(IServiceProvider serviceProvider, string name) =>
        serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("WeatherService.Resilience." + name);

    /// <summary>Transient SQL failures worth retrying / tripping the breaker.</summary>
    private static bool IsSqlFailure(Exception ex) =>
        ex is SqlException or DbException or TimeoutException;

    /// <summary>
    /// Upstream failures the breaker counts (<c>record-exceptions: java.io.IOException</c>). A 404 is
    /// surfaced as <see cref="FileNotFoundException"/> and is deliberately excluded — an unpublished
    /// feed is a normal skip, not an outage.
    /// </summary>
    private static bool IsFeedRecordable(Exception ex)
    {
        if (ex is FileNotFoundException)
        {
            return false;
        }

        return ex switch
        {
            HttpRequestException => true,
            TimeoutException => true,
            // A response body that dies mid-read (premature EOF, chunked-encoding error) surfaces as an
            // HttpIOException : IOException from the CONTENT read, never as an HttpRequestException.
            // RateLimitedException is also an IOException, so an exhausted 429 counts here — matching Java.
            IOException => true,
            // HttpClient surfaces a request/connect timeout as a cancellation with a TimeoutException inner.
            TaskCanceledException tce => tce.InnerException is TimeoutException,
            _ => false,
        };
    }

    /// <summary>
    /// Upstream failures worth another attempt. Everything the breaker records, minus the two cases
    /// listed under <c>ignore-exceptions</c> for the retry instances: an exhausted 429 (the
    /// <c>Retry-After</c> waits were already honoured inline) and an open breaker (retrying a
    /// short-circuit only burns the pass).
    /// </summary>
    private static bool IsFeedRetryable(Exception ex) =>
        ex is not RateLimitedException && ex is not BrokenCircuitException && IsFeedRecordable(ex);
}
