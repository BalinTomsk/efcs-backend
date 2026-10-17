using AdminService.Configuration;
using AdminService.Data;
using AdminService.Messaging;
using AdminService.Status;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Core;

namespace AdminService.Tests;

internal sealed class FakeQueue : IMessageQueue
{
    public Queue<string> Pending { get; } = new();
    public IReadOnlyDictionary<string, object>? DeclaredArguments { get; private set; }

    public Task DeclareAsync(string queue, IReadOnlyDictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        DeclaredArguments = arguments;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetAsync(string queue, int count, CancellationToken cancellationToken)
    {
        var batch = new List<string>();
        while (batch.Count < count && Pending.TryDequeue(out string? m)) batch.Add(m);
        return Task.FromResult<IReadOnlyList<string>>(batch);
    }
}

internal sealed class FakeRepository : IServiceStatusRepository
{
    public List<ServiceStatusMessage> Stored { get; } = [];
    public Exception? Failure { get; set; }

    public Task<bool> ReportAsync(ServiceStatusMessage message, CancellationToken cancellationToken)
    {
        if (Failure is not null) throw Failure;
        Stored.Add(message);
        return Task.FromResult(true);
    }
}

/// <summary>Covers the <c>fishfind.service-status.v1</c> contract and the consumer's batching.</summary>
public class ServiceStatusTests
{
    private static string Message(string evt = "heartbeat", string service = "waterservice", string machine = "csnode",
        string sent = "2026-10-16T13:05:00Z", string? action = "\"lastActionUtc\":\"2026-10-16T13:04:58Z\",\"lastAction\":\"station 05BH004\",") =>
        "{\"type\":\"fishfind.service-status.v1\",\"event\":\"" + evt + "\",\"service\":\"" + service + "\",\"machine\":\"" + machine
        + "\",\"version\":\"10.3.1\",\"processStartUtc\":\"2026-10-16T12:00:00Z\"," + action + "\"sentUtc\":\"" + sent + "\"}";

    private static (ServiceStatusConsumer Consumer, FakeQueue Queue, FakeRepository Repo) NewConsumer()
    {
        var queue = new FakeQueue();
        var repo = new FakeRepository();
        var consumer = new ServiceStatusConsumer(queue, repo, Options.Create(new AdminOptions()),
            NullLogger<ServiceStatusConsumer>.Instance);
        return (consumer, queue, repo);
    }

    [Test]
    public async Task Parse_ReadsEveryField_AsUtc()
    {
        ServiceStatusMessage? m = ServiceStatusMessage.TryParse(Message(), out string? error);

        await Assert.That(error).IsNull();
        await Assert.That(m!.Event).IsEqualTo("heartbeat");
        await Assert.That(m.Service).IsEqualTo("waterservice");
        await Assert.That(m.Machine).IsEqualTo("csnode");
        await Assert.That(m.Version).IsEqualTo("10.3.1");
        await Assert.That(m.ProcessStartUtc).IsEqualTo(new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc));
        await Assert.That(m.SentUtc.Kind).IsEqualTo(DateTimeKind.Utc);
        await Assert.That(m.LastAction).IsEqualTo("station 05BH004");
    }

    [Test]
    public async Task Parse_LowerCasesNames_AndAllowsNoAction()
    {
        ServiceStatusMessage? m = ServiceStatusMessage.TryParse(Message(service: " DocApi ", machine: "Debian-JNode", action: ""), out _);

        await Assert.That(m!.Service).IsEqualTo("docapi");
        await Assert.That(m.Machine).IsEqualTo("debian-jnode");
        await Assert.That(m.LastActionUtc).IsNull();
        await Assert.That(m.LastAction).IsNull();
    }

    [Test]
    public async Task Parse_CutsLongAction_To256()
    {
        string action = "\"lastActionUtc\":\"2026-10-16T13:04:58Z\",\"lastAction\":\"" + new string('x', 400) + "\",";
        ServiceStatusMessage? m = ServiceStatusMessage.TryParse(Message(action: action), out _);

        await Assert.That(m!.LastAction!.Length).IsEqualTo(256);
    }

    [Test]
    [Arguments("{\"type\":\"other\"}")]
    [Arguments("not json")]
    [Arguments("[]")]
    public async Task Parse_RejectsForeignBodies(string body)
    {
        ServiceStatusMessage? m = ServiceStatusMessage.TryParse(body, out string? error);

        await Assert.That(m).IsNull();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Parse_RejectsUnknownEvent_AndMissingTimes()
    {
        await Assert.That(ServiceStatusMessage.TryParse(Message(evt: "reboot"), out _)).IsNull();
        await Assert.That(ServiceStatusMessage.TryParse(Message(sent: "yesterday"), out _)).IsNull();
        await Assert.That(ServiceStatusMessage.TryParse(Message(service: ""), out _)).IsNull();
    }

    [Test]
    public async Task Poll_StoresOnlyTheNewestMessage_PerServiceAndMachine()
    {
        (ServiceStatusConsumer consumer, FakeQueue queue, FakeRepository repo) = NewConsumer();
        queue.Pending.Enqueue(Message(evt: "start", sent: "2026-10-16T12:00:01Z"));
        queue.Pending.Enqueue(Message(sent: "2026-10-16T12:02:00Z"));
        queue.Pending.Enqueue(Message(sent: "2026-10-16T12:01:00Z"));
        queue.Pending.Enqueue(Message(machine: "ctrl"));
        queue.Pending.Enqueue("garbage");

        bool healthy = await consumer.PollOnceAsync(CancellationToken.None);

        await Assert.That(healthy).IsTrue();
        await Assert.That(repo.Stored.Count).IsEqualTo(2);
        ServiceStatusMessage csnode = repo.Stored.Single(m => m.Machine == "csnode");
        await Assert.That(csnode.SentUtc).IsEqualTo(new DateTime(2026, 10, 16, 12, 2, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task Poll_ReportsUnhealthy_WhenTheDatabaseFails()
    {
        (ServiceStatusConsumer consumer, FakeQueue queue, FakeRepository repo) = NewConsumer();
        repo.Failure = new InvalidOperationException("db down");
        queue.Pending.Enqueue(Message());

        bool healthy = await consumer.PollOnceAsync(CancellationToken.None);

        await Assert.That(healthy).IsFalse();
        await Assert.That(queue.Pending.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MachineName_UsesConfiguredName_LowerCased()
    {
        await Assert.That(SelfStatusReporter.MachineName(" CSNode ")).IsEqualTo("csnode");
        await Assert.That(SelfStatusReporter.MachineName(null)).IsEqualTo(Environment.MachineName.ToLowerInvariant());
    }
}
