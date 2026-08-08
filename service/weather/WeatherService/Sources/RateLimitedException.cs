namespace WeatherService.Sources;

/// <summary>
/// Raised when a provider keeps returning HTTP 429 after the configured number of
/// <c>Retry-After</c>-honouring attempts.
///
/// <para>It is an <see cref="IOException"/> so the circuit breaker records it as a failure, but it is
/// excluded from retry — the waits have already been honoured inline, so another round of backoff
/// would only burn the pass. See <c>ResiliencePipelines.IsFeedRetryable</c>.</para>
/// </summary>
public class RateLimitedException : IOException
{
    public RateLimitedException(string message)
        : base(message)
    {
    }

    public RateLimitedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
