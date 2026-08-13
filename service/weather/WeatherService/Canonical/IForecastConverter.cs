namespace WeatherService.Canonical;

/// <summary>
/// Converts one provider's raw document into the canonical envelope.
///
/// <para>This is where provider knowledge now lives. Previously each provider needed its own T-SQL
/// parser inside a trigger, which could not raise — an error there would abort the worker's
/// <c>UPDATE</c> and discard the payload it had just fetched — so an unparseable document produced no
/// rows and no error. A converter runs in the worker, so it <b>throws</b>, and the failure is logged,
/// counted and retried like any other station failure.</para>
///
/// <para>Implementations must be pure: given the same raw document and station they produce the same
/// envelope, with no I/O and no clock beyond the injected one. That is what makes them testable
/// against recorded provider payloads.</para>
///
/// <para>Mirrors <c>com.fishfind.weather.canonical.ForecastConverter</c>.</para>
/// </summary>
public interface IForecastConverter
{
    /// <summary>Stable provider name recorded in the envelope, e.g. <c>visual-crossing</c>.</summary>
    string Provider { get; }

    /// <summary>Provider identity stored in <c>dbo.ows_meteo.type</c>; see <see cref="WeatherSourceType"/>.</summary>
    int ProviderType { get; }

    /// <summary>
    /// Converts a raw provider document into the canonical envelope.
    /// </summary>
    /// <param name="rawJson">the provider's response, verbatim — embedded in the envelope as <c>raw</c></param>
    /// <param name="mli">the water gauge this payload was fetched for</param>
    /// <exception cref="ForecastConversionException">
    /// the document is not the shape this provider produces, or carries no usable forecast day
    /// </exception>
    CanonicalForecast Convert(string rawJson, string mli);
}
