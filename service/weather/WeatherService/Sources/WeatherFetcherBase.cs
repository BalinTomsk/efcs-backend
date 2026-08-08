using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using WeatherService.Configuration;

namespace WeatherService.Sources;

/// <summary>
/// Shared transport for every upstream weather provider: issue one GET, honour <c>Retry-After</c> on
/// an HTTP 429 inline, bound the response size, and check the body is JSON before it is handed to the
/// database — all inside the provider's retry + circuit-breaker pipeline and behind its rate limiter.
///
/// <para>The five Java fetchers repeat this logic verbatim, differing only in the URL they build, the
/// request headers they send, and the wording of their exception messages. Those three are the
/// abstract/virtual surface here; every message is composed to read exactly as its Java counterpart
/// did, so operators grepping logs across the two implementations see the same strings.</para>
///
/// <para><strong>The response body is persisted raw</strong> — nothing here parses or reshapes it.
/// That is also why the size cap and the shape guard exist: an unbounded read becomes an unbounded
/// INSERT, and an HTML error page returned with a 200 must not reach the database, where the
/// downstream procedures would choke on it.</para>
/// </summary>
public abstract class WeatherFetcherBase
{
    private const int HttpTooManyRequests = 429;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResiliencePipeline _pipeline;
    private readonly ProviderRateLimiter _rateLimiter;

    /// <param name="pipelineName">
    /// Name of both the Polly pipeline and the rate limiter for this provider — the two are always
    /// registered under the same key (see <see cref="ResiliencePipelines"/>).
    /// </param>
    protected WeatherFetcherBase(
        string pipelineName,
        IHttpClientFactory httpClientFactory,
        IOptions<WorkerOptions> options,
        ResiliencePipelineProvider<string> pipelineProvider,
        ProviderRateLimiters rateLimiters,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _pipeline = pipelineProvider.GetPipeline(pipelineName);
        _rateLimiter = rateLimiters.Get(pipelineName);
        Options = options.Value;
        Log = logger;
    }

    /// <summary>Display name used in every log line and exception message, e.g. <c>Open-Meteo</c>.</summary>
    protected abstract string ProviderName { get; }

    protected WorkerOptions Options { get; }

    protected ILogger Log { get; }

    /// <summary>
    /// The per-request labels that make a provider's messages read the way its Java counterpart's did.
    /// </summary>
    /// <param name="Url">Fully-built request URL.</param>
    /// <param name="NotPublishedMessage">
    /// Complete message for an HTTP 404 — providers word this differently ("feed"/"observation",
    /// identified by URL, station id, or coordinates).
    /// </param>
    /// <param name="Suffix">
    /// Trailing identification appended to every other message, e.g. <c>" for URL https://…"</c> or
    /// <c>" for station KNYC"</c>. Empty for providers whose messages carry no identifier.
    /// </param>
    protected sealed record FetchTarget(string Url, string NotPublishedMessage, string Suffix);

    /// <summary>Adds the provider's request headers (User-Agent, Accept, …).</summary>
    protected abstract void ConfigureRequest(HttpRequestMessage request);

    /// <summary>
    /// Provider-specific status handling that runs before the 429 and non-200 checks. Default: none.
    /// </summary>
    protected virtual void ValidateStatus(HttpResponseMessage response, FetchTarget target)
    {
    }

    /// <summary>
    /// Provider-specific body validation. Default: the cheap JSON-object shape guard, which is all the
    /// inspection a verbatim-stored payload gets.
    /// </summary>
    protected virtual void ValidateBody(string body, FetchTarget target) => RequireJsonObjectShape(body, target);

    /// <summary>
    /// Runs one fetch through the provider's rate limiter, circuit breaker, and retry policy.
    /// </summary>
    protected async Task<string> ExecuteFetchAsync(FetchTarget target, CancellationToken ct) =>
        await _pipeline.ExecuteAsync(async token =>
        {
            // Innermost strategy, matching Resilience4j's Retry > CircuitBreaker > RateLimiter nesting:
            // every retry attempt acquires its own permit.
            await _rateLimiter.AcquireAsync(token).ConfigureAwait(false);
            return await SendAsync(target, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    private async Task<string> SendAsync(FetchTarget target, CancellationToken ct)
    {
        int rateLimitWaits = 0;

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
            ConfigureRequest(request);

            using HttpResponseMessage response = await HttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Not an error: this station simply has no published feed. Skipped, never retried.
                throw new FileNotFoundException(target.NotPublishedMessage);
            }

            ValidateStatus(response, target);

            if ((int)response.StatusCode == HttpTooManyRequests)
            {
                if (rateLimitWaits >= Options.RateLimit.MaxRetries)
                {
                    throw new RateLimitedException(
                        $"{ProviderName} rate limited (429) after {rateLimitWaits} waits{target.Suffix}");
                }

                long waitMs = RetryAfterMillis(response);
                rateLimitWaits++;
                Log.LogWarning(
                    "{Provider} rate limited (429). Honouring Retry-After. waitMs={WaitMs} attempt={Attempt}",
                    ProviderName, waitMs, rateLimitWaits);
                await HonourRetryAfterAsync(waitMs, ct).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new HttpRequestException(
                    $"{ProviderName} returned HTTP {(int)response.StatusCode}{target.Suffix}",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            string body = await ReadBoundedBodyAsync(response, target, ct).ConfigureAwait(false);
            ValidateBody(body, target);
            return body;
        }
    }

    /// <summary>
    /// The shared pooled client. Timeouts and decompression are configured once at registration; each
    /// call gets a fresh lightweight wrapper over the same pooled handler.
    /// </summary>
    private HttpClient HttpClient => _httpClientFactory.CreateClient(ServiceRegistration.WeatherHttpClient);

    /// <summary>
    /// Reads at most <see cref="WorkerOptions.MaxResponseBytes"/> + 1 bytes, so an oversized body is
    /// detected without ever being fully buffered.
    /// </summary>
    private async Task<string> ReadBoundedBodyAsync(
        HttpResponseMessage response, FetchTarget target, CancellationToken ct)
    {
        int max = Options.MaxResponseBytes;
        byte[] buffer = new byte[max + 1];
        int total = 0;

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
        }

        if (total > max)
        {
            throw new IOException($"{ProviderName} response exceeded {max} bytes{target.Suffix}");
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>
    /// Cheap shape guard, not JSON parsing: the body is persisted raw, but a 200 response that is not a
    /// JSON object (an HTML error or captive-portal page) must not reach the database.
    /// </summary>
    protected void RequireJsonObjectShape(string body, FetchTarget target)
    {
        string trimmed = body.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            throw new IOException($"{ProviderName} returned a non-JSON body{target.Suffix}");
        }
    }

    /// <summary>Parses <c>Retry-After</c> (delta-seconds or HTTP-date), clamped to [0, max].</summary>
    private long RetryAfterMillis(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return ClampWait(Options.RateLimit.DefaultWaitMs);
        }
        if (retryAfter.Delta is { } delta)
        {
            return ClampWait((long)delta.TotalMilliseconds);
        }
        if (retryAfter.Date is { } date)
        {
            return ClampWait((long)(date - DateTimeOffset.UtcNow).TotalMilliseconds);
        }
        return ClampWait(Options.RateLimit.DefaultWaitMs);
    }

    private long ClampWait(long ms) => Math.Max(0L, Math.Min(ms, Options.RateLimit.MaxWaitMs));

    private async Task HonourRetryAfterAsync(long ms, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(ms), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // RateLimitedException is excluded from retry, so a cancellation (typically shutdown) is not
            // followed by further backoff attempts.
            throw new RateLimitedException(
                $"Interrupted while waiting out {ProviderName} Retry-After", ex);
        }
    }

    /// <summary>Strips any trailing slashes from a configured base URL.</summary>
    protected static string TrimBaseUrl(string baseUrl) => baseUrl.TrimEnd('/');
}
