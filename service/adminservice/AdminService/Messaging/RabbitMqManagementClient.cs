using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace AdminService.Messaging;

/// <summary>
/// RabbitMQ through its <strong>management HTTP API</strong> — the transport the rest of the platform already
/// uses (the frontend's <c>AccountEventPublisher</c>, cproxy's <c>rabbit_event_consumer.cpp</c>, news_idx's
/// consumer). One HTTPS endpoint and one credential, no AMQP port to open.
/// </summary>
public interface IMessageQueue
{
    /// <summary>
    /// Declares the queue durable (idempotent; a no-op when it exists with the same arguments). Only adminservice
    /// declares <c>fishfind.servicestatus.events</c>; the reporting services just publish to it.
    /// </summary>
    Task DeclareAsync(string queue, IReadOnlyDictionary<string, object> arguments, CancellationToken cancellationToken);

    /// <summary>Takes up to <paramref name="count"/> messages off the queue (acknowledged on receipt).</summary>
    Task<IReadOnlyList<string>> GetAsync(string queue, int count, CancellationToken cancellationToken);
}

public sealed class RabbitMqManagementClient : IMessageQueue
{
    private readonly HttpClient _http;

    /// <param name="http">Configured with the management base address and basic auth (see ServiceRegistration).</param>
    public RabbitMqManagementClient(HttpClient http)
    {
        _http = http;
    }

    public static void Configure(HttpClient http, string managementUrl, string username, string password, TimeSpan timeout)
    {
        http.BaseAddress = new Uri(managementUrl.TrimEnd('/') + "/");
        http.Timeout = timeout;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
    }

    public async Task DeclareAsync(string queue, IReadOnlyDictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.PutAsJsonAsync(
            QueuePath(queue), new { durable = true, auto_delete = false, arguments }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<string>> GetAsync(string queue, int count, CancellationToken cancellationToken)
    {
        // ack_requeue_false = take and acknowledge, cproxy's mode. Status messages are superseded by the next
        // heartbeat, so one that then fails to store is not re-published (see ServiceStatusConsumer).
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            QueuePath(queue) + "/get",
            new { count, ackmode = "ack_requeue_false", encoding = "auto", truncate = 100_000 },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        List<ManagementMessage>? messages = await response.Content
            .ReadFromJsonAsync<List<ManagementMessage>>(cancellationToken).ConfigureAwait(false);

        return (messages ?? [])
            .Select(m => m.PayloadEncoding == "base64"
                ? Encoding.UTF8.GetString(Convert.FromBase64String(m.Payload ?? string.Empty))
                : m.Payload ?? string.Empty)
            .ToList();
    }

    private static string QueuePath(string queue) => "api/queues/%2F/" + Uri.EscapeDataString(queue);

    private sealed class ManagementMessage
    {
        [JsonPropertyName("payload")]
        public string? Payload { get; set; }

        [JsonPropertyName("payload_encoding")]
        public string? PayloadEncoding { get; set; }
    }
}
