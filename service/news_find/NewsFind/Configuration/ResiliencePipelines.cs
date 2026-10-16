using System.Data.Common;
using Grpc.Core;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace NewsFind.Configuration;

/// <summary>
/// Named Polly pipelines — one per dependency, so a Qdrant outage never opens the MySQL breaker or the
/// other way round. Same settings as the siblings' <c>sql</c> pipeline: the breaker is outermost (an open
/// breaker short-circuits instead of being retried) and all retries of one call count as one outcome.
/// </summary>
public static class ResiliencePipelines
{
    public const string MySql = "mysql";
    public const string Qdrant = "qdrant";

    public static IServiceCollection AddNewsFindResiliencePipelines(this IServiceCollection services)
    {
        Add(services, MySql, ex => ex is DbException or TimeoutException);
        Add(services, Qdrant, ex => ex is RpcException { StatusCode: not (StatusCode.InvalidArgument or StatusCode.NotFound) }
            or TimeoutException or HttpRequestException);
        return services;
    }

    private static void Add(IServiceCollection services, string name, Func<Exception, bool> transient)
    {
        services.AddResiliencePipeline(name, (builder, context) =>
        {
            ILogger logger = context.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger("NewsFind.Resilience." + name);
            PredicateBuilder<object> handle = new PredicateBuilder().Handle<Exception>(transient);

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
                })
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 2, // 1 initial + 2 retries = 3 attempts
                    Delay = TimeSpan.FromSeconds(2),
                    BackoffType = DelayBackoffType.Constant,
                    ShouldHandle = handle,
                });
        });
    }
}
