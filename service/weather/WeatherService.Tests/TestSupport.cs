using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.Registry;
using WeatherService.Configuration;
using WeatherService.Data;
using WeatherService.Domain;
using WeatherService.Reporting;
using WeatherService.Sources;

namespace WeatherService.Tests;

/// <summary>
/// Shared fakes for the suite. Every fetcher/worker test runs against an EMPTY resilience pipeline, so
/// the assertions are about classification and control flow only and never sit through real retry
/// delays or breaker windows.
/// </summary>
internal static class TestSupport
{
    /// <summary>A pipeline registry whose named pipelines do nothing (no retry, no breaker).</summary>
    public static ResiliencePipelineProvider<string> EmptyPipelines()
    {
        var registry = new ResiliencePipelineRegistry<string>();
        foreach (string name in ResiliencePipelines.FeedPipelines)
        {
            registry.TryAddBuilder(name, (_, _) => { });
        }
        registry.TryAddBuilder(ResiliencePipelines.Sql, (_, _) => { });
        return registry;
    }

    /// <summary>Worker options with the timings dialled down so tests never really wait.</summary>
    public static IOptions<WorkerOptions> WorkerConfig(Action<WorkerOptions>? customise = null)
    {
        var options = new WorkerOptions();
        options.RateLimit.MaxRetries = 2;
        options.RateLimit.DefaultWaitMs = 1;
        options.RateLimit.MaxWaitMs = 50;
        customise?.Invoke(options);
        return Options.Create(options);
    }

    public static StationRef Station(string mli = "MLI-1", double lat = 47.5, double lon = -122.3, string state = "WA") =>
        new(mli, lat, lon, state);

    /// <summary>Records the payloads a processor saves, without touching a database.</summary>
    public sealed class FakeWeatherDataRepository()
        : WeatherDataRepository(null!, EmptyPipelines(), NullLogger<WeatherDataRepository>.Instance)
    {
        public List<(string Mli, string Json, int SourceType)> Saved { get; } = [];

        public override Task SaveStationDataAsync(
            string mli, string jsonData, int sourceType, CancellationToken ct = default)
        {
            Saved.Add((mli, jsonData, sourceType));
            return Task.CompletedTask;
        }
    }

    /// <summary>Serves canned responses to the fetchers and remembers the last request it saw.</summary>
    public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    public sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Captures the weekly report instead of sending it.</summary>
    public sealed class RecordingEmailSender : IEmailSender
    {
        public bool IsConfigured => true;

        public List<EmailMessage> Sent { get; } = [];

        public Exception? ThrowOnSend { get; set; }

        public Task SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            if (ThrowOnSend is not null)
            {
                throw ThrowOnSend;
            }
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>Returns a fixed incident list, so the report can be tested without a real state dir.</summary>
    public sealed class FakeLifecycleTracker(params IncidentEntry[] incidents)
        : ServiceLifecycleTracker(
            Options.Create(new LifecycleOptions()),
            NullLogger<ServiceLifecycleTracker>.Instance)
    {
        public override IReadOnlyList<IncidentEntry> RecentIncidents() => incidents;
    }

    /// <summary>Builds a fetcher of type <typeparamref name="T"/> over a stub transport.</summary>
    public static T Fetcher<T>(
        HttpMessageHandler handler,
        Func<IHttpClientFactory, IOptions<WorkerOptions>, ResiliencePipelineProvider<string>, ProviderRateLimiters, T> create,
        Action<WorkerOptions>? customise = null)
        where T : WeatherFetcherBase =>
        create(new StubHttpClientFactory(handler), WorkerConfig(customise), EmptyPipelines(), new ProviderRateLimiters());
}
