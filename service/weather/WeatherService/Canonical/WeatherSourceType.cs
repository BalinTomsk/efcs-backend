namespace WeatherService.Canonical;

/// <summary>
/// Provider identity stored in <c>dbo.ows_meteo.type</c>.
///
/// <para>This column used to be a routing key: the database chose a T-SQL parser from it, and because
/// every provider was stamped <c>2</c> the parser had to guess the document's shape. Four providers'
/// payloads were indistinguishable from Open-Meteo and were silently discarded. It is now
/// <em>provenance</em> — which provider served this station — while the shape is declared by the
/// canonical envelope itself.</para>
///
/// <para>The values are a contract with <c>dbo.TR_ows_meteo</c>; see <c>envfish-db/CLAUDE.md</c>.
/// Types <see cref="WeatherGov"/> onward carry observations rather than forecasts on the legacy path
/// and are deliberately unrouted there, so a payload only becomes forecast rows once its converter
/// emits an envelope.</para>
///
/// <para>Mirrors <c>com.fishfind.weather.canonical.WeatherSourceType</c> in the Java service, which is
/// the reference implementation.</para>
/// </summary>
public static class WeatherSourceType
{
    /// <summary>The Weather Company / Weather Underground v3 daily forecast (<c>$.daypart[]</c>).</summary>
    public const int TwcDaily = 1;

    /// <summary>Open-Meteo (<c>$.hourly</c> + <c>$.daily</c>). Metric at source.</summary>
    public const int OpenMeteo = 2;

    /// <summary>Visual Crossing timeline (<c>$.days[]</c>). Requested in US units.</summary>
    public const int VisualCrossing = 4;

    /// <summary>weather.gov / NWS (GeoJSON).</summary>
    public const int WeatherGov = 5;

    /// <summary>Environment Canada / MSC SWOB observations.</summary>
    public const int EnvironmentCanada = 6;

    /// <summary>Weather Underground personal-station observations (<c>$.observations[]</c>).</summary>
    public const int WundergroundObservations = 7;

    /// <summary>Google Weather.</summary>
    public const int GoogleWeather = 8;
}
