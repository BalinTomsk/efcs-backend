using System.Text.Json;
using System.Text.Json.Nodes;

namespace NewsIdx.Domain;

/// <summary>
/// One <c>fishfind.news-event.v1</c> message from the <c>fishfind.newsidx.events</c> queue. Same envelope as the
/// frontend's account events (<c>AccountEventPublisher.BasePayload</c>):
///
/// <code>
/// { "schema": "fishfind.news-event.v1", "eventId": "…", "eventType": "fishfind.news.article",
///   "action": "created|updated|published|unpublished|deleted", "aggregateId": "&lt;news_id&gt;",
///   "occurredUtc": "…", "source": "fishfind-frontend" }
/// </code>
///
/// <para>The event is a <em>notification</em>, not the data: it carries only the article id, and the indexer
/// reads the article from MySQL. So <c>action</c> is advisory — whatever it says, the article's current
/// state in the database decides whether it is indexed or removed. <c>attempt</c> is added by this service
/// when it re-queues a message that failed.</para>
/// </summary>
public sealed record NewsEvent(string EventId, string Action, string NewsId, int Attempt)
{
    public const string Schema = "fishfind.news-event.v1";
    public const string EventType = "fishfind.news.article";

    /// <summary>Parses a message body; <c>null</c> plus a reason when it is not a usable news event.</summary>
    public static NewsEvent? TryParse(string body, out string? error)
    {
        error = null;
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            error = "body is not JSON";
            return null;
        }

        if (root is null)
        {
            error = "body is not a JSON object";
            return null;
        }

        string? schema = Text(root, "schema");
        if (!string.Equals(schema, Schema, StringComparison.Ordinal))
        {
            error = $"unexpected schema '{schema}'";
            return null;
        }

        string? type = Text(root, "eventType");
        if (!string.Equals(type, EventType, StringComparison.Ordinal))
        {
            error = $"unexpected eventType '{type}'";
            return null;
        }

        string? id = Text(root, "aggregateId");
        if (!Guid.TryParse(id, out Guid newsId))
        {
            error = $"aggregateId '{id}' is not a news id";
            return null;
        }

        int attempt = root["attempt"] is JsonValue v && v.TryGetValue(out int a) && a > 0 ? a : 1;
        return new NewsEvent(
            Text(root, "eventId") ?? string.Empty,
            (Text(root, "action") ?? "changed").ToLowerInvariant(),
            // Qdrant keyword filters are case-sensitive and MySQL's collation is not, so every news id in
            // this service is the lower-case "D" form (see NewsIds.Normalize).
            newsId.ToString("D"),
            attempt);
    }

    /// <summary>The original body with <c>attempt</c> set — what a failed message is re-queued as.</summary>
    public static string WithAttempt(string body, int attempt)
    {
        JsonObject root = (JsonObject)JsonNode.Parse(body)!;
        root["attempt"] = attempt;
        return root.ToJsonString();
    }

    private static string? Text(JsonObject root, string name) =>
        root[name] is JsonValue value && value.TryGetValue(out string? s) ? s : null;
}
