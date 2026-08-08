using System.Threading.RateLimiting;

namespace WeatherService.Sources;

/// <summary>
/// Paces outbound calls to one upstream provider, mirroring a Resilience4j <c>RateLimiter</c>
/// instance: at most <c>limitForPeriod</c> calls per <c>limitRefreshPeriod</c>, with a caller waiting
/// up to <c>timeoutDuration</c> for a permit before the call is refused.
///
/// <para>This is deliberately not a Polly strategy. Polly's rate-limiter strategy queues on the
/// execution's own cancellation token with no separate acquisition deadline, which loses the
/// "wait a bounded time, then fail fast" behaviour these limits rely on. Applying it as the innermost
/// step inside the fetch delegate also reproduces Resilience4j's annotation nesting exactly
/// (Retry &gt; CircuitBreaker &gt; RateLimiter).</para>
/// </summary>
public sealed class ProviderRateLimiter : IDisposable
{
    private readonly FixedWindowRateLimiter _limiter;
    private readonly TimeSpan _acquireTimeout;

    /// <param name="provider">Pipeline/provider name, used only in the refusal message.</param>
    /// <param name="permitsPerWindow">Resilience4j <c>limit-for-period</c>.</param>
    /// <param name="window">Resilience4j <c>limit-refresh-period</c>.</param>
    /// <param name="acquireTimeout">Resilience4j <c>timeout-duration</c>.</param>
    public ProviderRateLimiter(string provider, int permitsPerWindow, TimeSpan window, TimeSpan acquireTimeout)
    {
        Provider = provider;
        _acquireTimeout = acquireTimeout;
        _limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitsPerWindow,
            Window = window,
            QueueLimit = int.MaxValue, // callers wait for a permit; the deadline below bounds the wait
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    }

    public string Provider { get; }

    /// <summary>
    /// Waits for one permit, for at most the configured timeout.
    /// </summary>
    /// <exception cref="RateLimitPermitTimeoutException">
    /// No permit became available in time. Deliberately not an <see cref="IOException"/>, so — like
    /// Resilience4j's <c>RequestNotPermitted</c> — it is neither retried nor counted against the
    /// circuit breaker; it simply fails the one station.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled (shutdown), as opposed to the acquisition deadline expiring.
    /// </exception>
    public async Task AcquireAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_acquireTimeout);

        RateLimitLease lease;
        try
        {
            lease = await _limiter.AcquireAsync(1, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RateLimitPermitTimeoutException(Provider, _acquireTimeout);
        }

        using (lease)
        {
            if (!lease.IsAcquired)
            {
                throw new RateLimitPermitTimeoutException(Provider, _acquireTimeout);
            }
        }
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// Thrown when a caller waited out the whole permit-acquisition timeout for a provider. The
/// Resilience4j <c>RequestNotPermitted</c> equivalent — see <see cref="ProviderRateLimiter.AcquireAsync"/>
/// for why it is intentionally outside the <see cref="IOException"/> hierarchy.
/// </summary>
public sealed class RateLimitPermitTimeoutException : Exception
{
    public RateLimitPermitTimeoutException(string provider, TimeSpan timeout)
        : base($"Timed out after {timeout.TotalSeconds:0.###}s waiting for a {provider} rate-limiter permit.")
    {
        Provider = provider;
    }

    public string Provider { get; }
}
