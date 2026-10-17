using System.Globalization;
using System.Text.Json;

namespace AdminService.Status;

/// <summary>
/// One <c>fishfind.service-status.v1</c> message, as every backend service publishes it to
/// <c>fishfind.servicestatus.events</c>:
/// <code>
/// {"type":"fishfind.service-status.v1","event":"heartbeat","service":"waterservice","machine":"csnode",
///  "version":"10.3.1","processStartUtc":"2026-10-16T12:00:00Z","sentUtc":"2026-10-16T13:05:00Z",
///  "lastActionUtc":"2026-10-16T13:04:58Z","lastAction":"station 05BH004"}
/// </code>
/// <c>event</c> is <c>start</c> (process came up), <c>heartbeat</c> (every minute while running) or
/// <c>stop</c> (clean shutdown). <c>lastActionUtc</c>/<c>lastAction</c> are optional.
/// </summary>
public sealed record ServiceStatusMessage(
    string Event,
    string Service,
    string Machine,
    string? Version,
    DateTime ProcessStartUtc,
    DateTime SentUtc,
    DateTime? LastActionUtc,
    string? LastAction)
{
    public const string MessageType = "fishfind.service-status.v1";

    /// <summary>Column widths of dbo.ServiceStatus; longer values are refused or cut, never truncated by SQL.</summary>
    internal const int MaxService = 64, MaxMachine = 128, MaxVersion = 32, MaxAction = 256;

    private static readonly HashSet<string> Events = new(StringComparer.Ordinal) { "start", "heartbeat", "stop" };

    /// <summary>Parses and validates a message body; <c>null</c> with <paramref name="error"/> set when unusable.</summary>
    public static ServiceStatusMessage? TryParse(string body, out string? error)
    {
        error = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "not a JSON object";
                return null;
            }

            if (Text(root, "type") != MessageType)
            {
                error = "type is not " + MessageType;
                return null;
            }

            string? evt = Text(root, "event");
            if (evt is null || !Events.Contains(evt))
            {
                error = "event must be start, heartbeat or stop";
                return null;
            }

            string? service = Text(root, "service")?.Trim().ToLowerInvariant();
            string? machine = Text(root, "machine")?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(service) || service.Length > MaxService
                || string.IsNullOrEmpty(machine) || machine.Length > MaxMachine)
            {
                error = "service and machine are required (max 64 / 128 chars)";
                return null;
            }

            DateTime? started = Utc(root, "processStartUtc");
            DateTime? sent = Utc(root, "sentUtc");
            if (started is null || sent is null)
            {
                error = "processStartUtc and sentUtc must be ISO-8601 UTC timestamps";
                return null;
            }

            DateTime? actionUtc = Utc(root, "lastActionUtc");
            string? action = actionUtc is null ? null : Cut(Text(root, "lastAction"), MaxAction);

            return new ServiceStatusMessage(evt, service, machine, Cut(Text(root, "version"), MaxVersion),
                started.Value, sent.Value, actionUtc, action);
        }
        catch (JsonException ex)
        {
            error = "invalid JSON: " + ex.Message;
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? Utc(JsonElement root, string name)
    {
        string? text = Text(root, name);
        return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset value)
            ? value.UtcDateTime
            : null;
    }

    private static string? Cut(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        value = value.Trim();
        return value.Length <= max ? value : value[..max];
    }
}
