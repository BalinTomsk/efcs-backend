using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WeatherService.Canonical;

/// <summary>
/// The canonical forecast envelope stored in <c>dbo.ows_meteo.ows</c>, whichever provider was fetched.
///
/// <code>
/// { "schema":"fishfind.weather.forecast/v1", "provider":"visual-crossing", "providerType":4,
///   "mli":"13068500", "fetchedUtc":"2026-08-13T04:12:00Z",
///   "days":[ … ],
///   "raw":{ …the provider's original document… } }
/// </code>
///
/// <para><b>raw is not decoration.</b> Diagnosing the 2026-08-12 Visual Crossing outage only worked
/// because the provider's actual document was still in the table and could be replayed against the
/// procedure. Keeping it inside the envelope preserves that, and avoids an <c>ALTER TABLE</c> on a
/// replicated table. <c>sp_ows_meteo_canonical</c> ignores it.</para>
///
/// <para><b>schema is the contract.</b> The database routes on it and treats a version it does not know
/// as a no-op rather than a guess, so this service can be deployed ahead of the database without a
/// payload being half-parsed. Bump <see cref="Schema"/> only alongside a database that understands it.</para>
///
/// <para>Mirrors <c>com.fishfind.weather.canonical.CanonicalForecast</c>.</para>
/// </summary>
public sealed record CanonicalForecast
{
    /// <summary>Envelope version understood by <c>dbo.sp_ows_meteo_canonical</c>.</summary>
    public const string Schema = "fishfind.weather.forecast/v1";

    /// <summary>Serializer settings that produce the exact envelope the database expects.</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [JsonPropertyName("schema")]
    public string SchemaVersion { get; init; } = Schema;

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("providerType")]
    public required int ProviderType { get; init; }

    [JsonPropertyName("mli")]
    public required string Mli { get; init; }

    [JsonPropertyName("fetchedUtc")]
    public required DateTimeOffset FetchedUtc { get; init; }

    [JsonPropertyName("days")]
    public required IReadOnlyList<ForecastDay> Days { get; init; }

    /// <summary>The provider's original document, kept so a stored payload stays inspectable and replayable.</summary>
    [JsonPropertyName("raw")]
    public JsonNode? Raw { get; init; }

    /// <summary>Serialises the envelope exactly as the database expects it.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}
