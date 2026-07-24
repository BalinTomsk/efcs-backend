using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using WaterService.Processing;

namespace WaterService.Tests;

public class StationProcessorBaseTests
{
    [Test]
    public async Task SuccessPath_ReturnsProcessed_AndBoolCompatibilityReturnsTrue()
    {
        var processor = new TestProcessor();

        await Assert.That(await processor.ProcessWithOutcomeAsync("02JE025", "QC", -5, CancellationToken.None))
            .IsEqualTo(ProcessingOutcome.Processed);
        await Assert.That(await processor.ProcessAsync("02JE025", "QC", -5, CancellationToken.None))
            .IsTrue();
        await Assert.That(processor.ProcessedCount).IsEqualTo(2);
    }

    [Test]
    public async Task FileNotFound_ReturnsSkipped_AndBoolCompatibilityReturnsFalse()
    {
        var processor = new TestProcessor
        {
            ToThrow = new FileNotFoundException("no feed"),
        };

        await Assert.That(await processor.ProcessWithOutcomeAsync("02JE025", "QC", -5, CancellationToken.None))
            .IsEqualTo(ProcessingOutcome.Skipped);
        await Assert.That(await processor.ProcessAsync("02JE025", "QC", -5, CancellationToken.None))
            .IsFalse();
    }

    [Test]
    public async Task HttpRequestServiceUnavailable_ReturnsHttp503Failure()
    {
        var processor = new TestProcessor
        {
            ToThrow = new HttpRequestException(
                "Service Unavailable",
                inner: null,
                statusCode: HttpStatusCode.ServiceUnavailable),
        };

        await Assert.That(await processor.ProcessWithOutcomeAsync("08313000", "NY", -5, CancellationToken.None))
            .IsEqualTo(ProcessingOutcome.FailedHttp503);
    }

    [Test]
    public async Task MessageContainingHttp503_ReturnsHttp503Failure()
    {
        var processor = new TestProcessor
        {
            ToThrow = new IOException("HTTP 503"),
        };

        await Assert.That(await processor.ProcessWithOutcomeAsync("02JE025", "QC", -5, CancellationToken.None))
            .IsEqualTo(ProcessingOutcome.FailedHttp503);
    }

    [Test]
    public async Task OtherException_ReturnsFailed()
    {
        var processor = new TestProcessor
        {
            ToThrow = new InvalidOperationException("bad payload"),
        };

        await Assert.That(await processor.ProcessWithOutcomeAsync("02JE025", "QC", -5, CancellationToken.None))
            .IsEqualTo(ProcessingOutcome.Failed);
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
