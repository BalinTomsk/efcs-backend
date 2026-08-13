namespace WeatherService.Canonical;

/// <summary>
/// A provider document could not be converted into the canonical envelope.
///
/// <para>The station processors already turn a failure into a counted, logged
/// <c>ProcessingOutcome</c>, so a provider that changes its response shape shows up as failing stations
/// in the cycle report instead of disappearing into a silent no-op the way it did when parsing happened
/// inside a database trigger.</para>
///
/// <para>Mirrors <c>com.fishfind.weather.canonical.ForecastConversionException</c>.</para>
/// </summary>
public class ForecastConversionException : Exception
{
    public ForecastConversionException(string message) : base(message)
    {
    }

    public ForecastConversionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
