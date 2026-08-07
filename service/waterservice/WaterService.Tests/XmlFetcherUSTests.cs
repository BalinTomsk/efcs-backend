using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.Registry;
using TUnit.Core;
using WaterService.Configuration;
using WaterService.Sources;

namespace WaterService.Tests;

/// <summary>
/// Covers how <see cref="XmlFetcherUS"/> classifies USGS transport failures — the part that decides
/// whether a station is retried, skipped, or burned for the cycle.
/// </summary>
public class XmlFetcherUSTests
{
    private const string Mli = "08313000";
    private const string State = "NY";

    [Test]
    public async Task ResponseBodyIoFailure_IsRetryableAndNamesTheStation()
    {
        // A USGS response that dies mid-body arrives AFTER the headers, so it surfaces from the content
        // read rather than from the request. Before this was classified it escaped the transient
        // handling entirely and burned the station for the whole cycle.
        var fetcher = NewFetcher(new StubHandler(_ => ThrowingBodyResponse()));

        HttpRequestException thrown = await CaptureAsync(() => fetcher.FetchAsync(State, Mli));

        await Assert.That(thrown.Message).Contains(Mli);
        await Assert.That(thrown.Message).Contains(State);
    }

    [Test]
    public async Task NotFound_IsSkippedNotFailed()
    {
        // 404 means the feed is not published for this station: skipped, never retried.
        var fetcher = NewFetcher(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        await Assert.That(() => fetcher.FetchAsync(State, Mli)).Throws<FileNotFoundException>();
    }

    [Test]
    public async Task SuccessfulResponse_ReturnsBody()
    {
        var fetcher = NewFetcher(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<timeSeriesResponse />"),
        }));

        await Assert.That(await fetcher.FetchAsync(State, Mli)).IsEqualTo("<timeSeriesResponse />");
    }

    // --- helpers -----------------------------------------------------------------------------------

    /// <summary>
    /// Builds a fetcher over a stub transport and an EMPTY resilience pipeline, so these tests assert
    /// classification only and never sit through the real retry delays.
    /// </summary>
    private static XmlFetcherUS NewFetcher(HttpMessageHandler handler)
    {
        var registry = new ResiliencePipelineRegistry<string>();
        registry.TryAddBuilder(ResiliencePipelines.UsFeed, (_, _) => { });

        return new XmlFetcherUS(new StubHttpClientFactory(handler), registry, NullLogger<XmlFetcherUS>.Instance);
    }

    private static HttpResponseMessage ThrowingBodyResponse() =>
        new(HttpStatusCode.OK) { Content = new ThrowingContent() };

    private static async Task<HttpRequestException> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (HttpRequestException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("expected an HttpRequestException, but none was thrown");
    }

    /// <summary>Response content whose body read fails, like a connection dropped mid-payload.</summary>
    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException("The response ended prematurely.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
