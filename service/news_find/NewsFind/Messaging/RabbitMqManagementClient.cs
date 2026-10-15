using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewsFind.Messaging;

/// <summary>
/// RabbitMQ through its <strong>management HTTP API</strong> — the transport the rest of the platform already
/// uses: the frontend publishes with <c>POST /api/exchanges/%2F/amq.default/publish</c>
/// (<c>AccountEventPublisher</c>) and cproxy consumes by polling <c>POST /api/queues/%2F/{queue}/get</c>
/// (<c>rabbit_event_consumer.cpp</c>). One HTTPS endpoint and one credential, no AMQP port to open.
/// </summary>
public interface IMessageQueue
{
    /// <summary>Declares the queue durable (idempotent; a no-op when it exists with the same arguments).</summary>
    Task DeclareAsync(string queue, CancellationToken cancellationToken);

    /// <summary>Takes up to <paramref name="count"/> messages off the queue (acknowledged on receipt).</summary>
    Task<IReadOnlyList<string>> GetAsync(string queue, int count, CancellationToken cancellationToken);

    /// <summary>Publishes a persistent JSON message to the queue; <c>true</c> when RabbitMQ routed it.</summary>
    Task<bool> PublishAsync(string queue, string body, CancellationToken cancellationToken);
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

    public async Task DeclareAsync(string queue, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.PutAsJsonAsync(
            QueuePath(queue), new { durable = true, auto_delete = false, arguments = new { } }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<string>> GetAsync(string queue, int count, CancellationToken cancellationToken)
    {
        // ack_requeue_false = take and acknowledge, cproxy's mode. A message that then fails is re-published
        // by the consumer (with an attempt count) instead of being requeued in place.
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            QueuePath(queue) + "/get",
            new { count, ackmode = "ack_requeue_false", encoding = "auto", truncate = 1_000_000 },
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

    public async Task<bool> PublishAsync(string queue, string body, CancellationToken cancellationToken)
    {
        var envelope = new
        {
            properties = new { delivery_mode = 2, content_type = "application/json" },
            routing_key = queue,
            payload = body,
            payload_encoding = "string",
        };
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "api/exchanges/%2F/amq.default/publish", envelope, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using JsonDocument result = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.RootElement.TryGetProperty("routed", out JsonElement routed) && routed.GetBoolean();
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
