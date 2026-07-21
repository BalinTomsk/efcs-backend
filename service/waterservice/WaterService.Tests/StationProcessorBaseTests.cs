using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WaterService.Processing;
using Xunit;

namespace WaterService.Tests;

public class StationProcessorBaseTests
{
    [Fact]
    public async Task SuccessPath_ReturnsProcessed_AndBoolCompatibilityReturnsTrue()
    {
        var processor = new TestProcessor();

        Assert.Equal(
            ProcessingOutcome.Processed,
            await processor.ProcessWithOutcomeAsync("02JE025", "QC", -5, CancellationToken.None));
        Assert.True(await processor.ProcessAsync("02JE025", "QC", -5, CancellationToken.None));
        Assert.Equal(2, processor.ProcessedCount);
    }

    [Fact]
    public async Task FileNotFound_ReturnsSkipped_AndBoolCompatibilityReturnsFalse()
    {
        var processor = new TestProcessor
        {
            ToThrow = new FileNotFoundException("no feed"),
        };

        Assert.Equal(
            ProcessingOutcome.Skipped,
            await processor.ProcessWithOutcomeAsync("02JE025", "QC", -5, CancellationToken.None));
        Assert.False(await processor.ProcessAsync("02JE025", "QC", -5, CancellationToken.None));
    }

    [Fact]
    public async Task HttpRequestServiceUnavailable_ReturnsHttp503Failure()
    {
        var processor = new TestProcessor
        {
            ToThrow = new HttpRequestException(
                "Service Unavailable",
                inner: null,
                statusCode: HttpStatusCode.ServiceUnavailable),
        };

        Assert.Equal(
            ProcessingOutcome.FailedHttp503,
            await processor.ProcessWithOutcomeAsync("08313000", "NY", -5, CancellationToken.None));
    }

    [Fact]
    public async Task MessageContainingHttp503_ReturnsHttp503Failure()
    {
        var processor = new TestProcessor
        {
            ToThrow = new IOException("HTTP 503"),
        };

        Assert.Equal(
            ProcessingOutcome.FailedHttp503,
            await processor.ProcessWithOutcomeAsync("02JE025", "QC", -5, CancellationToken.None));
    }

    [Fact]
    public async Task OtherException_ReturnsFailed()
    {
        var processor = new TestProcessor
        {
            ToThrow = new InvalidOperationException("bad payload"),
        };

        Assert.Equal(
            ProcessingOutcome.Failed,
            await processor.ProcessWithOutcomeAsync("02JE025", "QC", -5, CancellationToken.None));
    }

    private sealed class TestProcessor : StationProcessorBase
    {
        public Exception? ToThrow { get; init; }

        public int ProcessedCount { get; private set; }

        protected override ILogger Logger => NullLogger.Instance;

        protected override string Country => "CA";

        protected override string MissingSourceDescription => "hydrometric CSV";

        protected override Task ProcessStationAsync(string mli, string state, int tz, CancellationToken ct)
        {
            if (ToThrow is not null)
            {
                throw ToThrow;
            }

            ProcessedCount++;
            return Task.CompletedTask;
        }
    }
}
