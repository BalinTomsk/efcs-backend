using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NewsIdx.Status;
using TUnit.Core;

namespace NewsIdx.Tests;

/// <summary>Qdrant's row on the admin status page, reported by news_idx on its behalf.</summary>
public class StatusTests
{
    private const string Telemetry =
        """{"result":{"id":"d867","app":{"name":"qdrant","version":"1.19.0","startup":"2026-10-20T22:52:46.280Z"},"collections":{"number_of_collections":2}},"status":"ok","time":0.0003}""";

    [Test]
    public async Task Telemetry_Parse_ReadsVersionAndUtcStartup()
    {
        QdrantTelemetry t = QdrantTelemetry.Parse(Telemetry);

        await Assert.That(t.Version).IsEqualTo("1.19.0");
        await Assert.That(t.StartupUtc).IsEqualTo(new DateTime(2026, 10, 20, 22, 52, 46, 280, DateTimeKind.Utc));
        await Assert.That(t.StartupUtc.Kind).IsEqualTo(DateTimeKind.Utc);
    }

    [Test]
    [Arguments("http://qdrant:6334", "http://qdrant:6333/")]
    [Arguments("http://localhost:6334/", "http://localhost:6333/")]
    [Arguments("https://q.example:443", "https://q.example/")]
    public async Task RestUrl_SwapsOnlyTheDefaultGrpcPort(string grpc, string rest)
    {
        await Assert.That(ServiceStatusRegistration.QdrantRestUrl(grpc)).IsEqualTo(rest);
    }

    [Test]
    public async Task Event_IsStart_UntilThatQdrantProcessWasReported()
    {
        var reporter = new QdrantStatusReporter(null!, new ServiceStatusSettings("news_idx", "ctrl", TimeSpan.FromMinutes(1)),
            NullLogger<QdrantStatusReporter>.Instance);

        await Assert.That(reporter.EventFor(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc))).IsEqualTo("start");
    }

    [Test]
    public async Task Body_NamesQdrant_WithItsOwnVersionAndStart()
    {
        var reporter = new QdrantStatusReporter(null!, new ServiceStatusSettings("news_idx", "ctrl", TimeSpan.FromMinutes(1)),
            NullLogger<QdrantStatusReporter>.Instance);
        var sent = new DateTime(2026, 10, 20, 23, 0, 0, DateTimeKind.Utc);

        using JsonDocument doc = JsonDocument.Parse(reporter.Body("heartbeat", QdrantTelemetry.Parse(Telemetry), sent));
        JsonElement m = doc.RootElement;

        await Assert.That(m.GetProperty("type").GetString()).IsEqualTo(ServiceStatusReporter.MessageType);
        await Assert.That(m.GetProperty("event").GetString()).IsEqualTo("heartbeat");
        await Assert.That(m.GetProperty("service").GetString()).IsEqualTo("qdrant");
        await Assert.That(m.GetProperty("machine").GetString()).IsEqualTo("ctrl");
        await Assert.That(m.GetProperty("version").GetString()).IsEqualTo("1.19.0");
        await Assert.That(m.GetProperty("processStartUtc").GetString()).IsEqualTo("2026-10-20T22:52:46.2800000Z");
        await Assert.That(m.GetProperty("lastAction").ValueKind).IsEqualTo(JsonValueKind.Null);
    }
}
