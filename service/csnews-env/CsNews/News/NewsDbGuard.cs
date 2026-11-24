using System.Data.Common;
using MySqlConnector;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace CsNews.News;

/// <summary>
/// Retry + circuit breaker around every MySQL call, the policy docapi's <c>sqlRetry</c>/<c>sqlBreaker</c> gave
/// these statements before they moved here: reads get one retry after 500 ms, and only for a fault a second
/// attempt can clear (a dropped handshake, a pool timeout); writes are never retried. One breaker for every
/// statement (50 % failures over 30 s, minimum 5 calls, open 30 s), so a dead database fails fast instead of
/// holding docapi's threads. Any database fault leaves here as <see cref="NewsStoreUnavailableException"/> (503).
/// </summary>
public sealed class NewsDbGuard
{
    private readonly ResiliencePipeline _breaker;
    private readonly ResiliencePipeline _retry;
    private readonly ILogger<NewsDbGuard> _logger;

    public NewsDbGuard(ILogger<NewsDbGuard> logger)
    {
        _logger = logger;
        _breaker = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsDatabaseFault),
            })
            .Build();
        _retry = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.FromMilliseconds(500),
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
            })
            .Build();
    }

    /// <summary>A read: breaker outermost, one retry inside it.</summary>
    public Task<T> ReadAsync<T>(string what, Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        RunAsync(what, token => _breaker.ExecuteAsync(
            t => _retry.ExecuteAsync(t2 => new ValueTask<T>(operation(t2)), t), token), ct);

    /// <summary>A write: breaker only. A retried write could run twice.</summary>
    public Task<T> WriteAsync<T>(string what, Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        RunAsync(what, token => _breaker.ExecuteAsync(t => new ValueTask<T>(operation(t)), token), ct);

    private async Task<T> RunAsync<T>(string what, Func<CancellationToken, ValueTask<T>> run, CancellationToken ct)
    {
        try
        {
            return await run(ct).ConfigureAwait(false);
        }
        catch (BrokenCircuitException ex)
        {
            throw new NewsStoreUnavailableException($"News database circuit open ({what})", ex);
        }
        catch (Exception ex) when (IsDatabaseFault(ex) && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "News database call failed: {What}", what);
            throw new NewsStoreUnavailableException($"News database call failed ({what})", ex);
        }
    }

    /// <summary>Counts against the breaker: anything the database or the network did, never our own bugs.</summary>
    internal static bool IsDatabaseFault(Exception ex) =>
        ex is DbException or TimeoutException or IOException or System.Net.Sockets.SocketException
        || (ex is InvalidOperationException && ex.InnerException is DbException or IOException);

    /// <summary>Worth a second attempt: connection-level faults only, never a SQL error.</summary>
    internal static bool IsTransient(Exception ex) => ex switch
    {
        MySqlException m => m.IsTransient || m.ErrorCode is MySqlErrorCode.UnableToConnectToHost,
        TimeoutException or IOException or System.Net.Sockets.SocketException => true,
        _ => false,
    };
}
