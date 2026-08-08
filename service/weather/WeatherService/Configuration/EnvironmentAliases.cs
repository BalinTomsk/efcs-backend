namespace WeatherService.Configuration;

/// <summary>
/// Maps the flat environment-variable names used by the Java service onto this service's
/// configuration keys, so one <c>.env</c> file (or one Docker <c>--env-file</c>) drives either
/// implementation unchanged.
///
/// <para>In Spring these were written inline as <c>${SMTP_HOST:}</c>-style placeholders on each
/// property. .NET's provider chain has no placeholder syntax, so the equivalent is an explicit alias
/// table applied as a high-precedence source: whatever the flat variable says wins over
/// <c>appsettings.json</c>, exactly as the placeholder did.</para>
///
/// <para>Only variables that are actually present and non-blank are mapped. A blank or absent one
/// contributes nothing, leaving the <c>appsettings.json</c> default in place — the alternative would
/// clobber real defaults with empty strings.</para>
/// </summary>
public static class EnvironmentAliases
{
    private static readonly (string Variable, string ConfigurationKey)[] Aliases =
    [
        ("SMTP_HOST", "Smtp:Host"),
        ("SMTP_PORT", "Smtp:Port"),
        ("SMTP_USERNAME", "Smtp:Username"),
        ("SMTP_PASSWORD", "Smtp:Password"),
        ("REPORT_EMAIL_TO", "Weather:Report:To"),
        ("REPORT_EMAIL_FROM", "Weather:Report:From"),
        ("WEATHER_GOV_ENABLE", "Weather:Worker:Enable:WeatherGov"),
        ("OPEN_METEO_ENABLE", "Weather:Worker:Enable:OpenMeteo"),
        ("VISUAL_CROSSING_ENABLE", "Weather:Worker:Enable:VisualCrossing"),
        ("GOOGLE_WEATHER_ENABLE", "Weather:Worker:Enable:GoogleWeather"),
        ("WEATHER_CANADA_ENABLE", "Weather:Worker:Enable:WeatherCanada"),
        ("WEATHER_GOV_TIMEOUT", "Weather:Worker:Timeout:WeatherGov"),
        ("OPEN_METEO_TIMEOUT", "Weather:Worker:Timeout:OpenMeteo"),
        ("VISUAL_CROSSING_TIMEOUT", "Weather:Worker:Timeout:VisualCrossing"),
        ("GOOGLE_WEATHER_TIMEOUT", "Weather:Worker:Timeout:GoogleWeather"),
        ("WEATHER_CANADA_TIMEOUT", "Weather:Worker:Timeout:WeatherCanada"),
        ("WEATHER_GOV_USER_AGENT", "Weather:Worker:WeatherGovUserAgent"),
        ("WEATHER_CANADA_USER_AGENT", "Weather:Worker:WeatherCanadaUserAgent"),
        ("WEATHER_CANADA_BBOX_RADIUS_DEGREES", "Weather:Worker:WeatherCanadaBboxRadiusDegrees"),
        ("VISUAL_CROSSING_API_KEY", "Weather:Worker:VisualCrossingApiKey"),
        ("GOOGLE_WEATHER_API_KEY", "Weather:Worker:GoogleWeatherApiKey"),
        ("WEATHER_GOV_DAILY_LIMIT", "Weather:Worker:DailyLimit:WeatherGov"),
        ("OPEN_METEO_DAILY_LIMIT", "Weather:Worker:DailyLimit:OpenMeteo"),
        ("VISUAL_CROSSING_DAILY_LIMIT", "Weather:Worker:DailyLimit:VisualCrossing"),
        ("GOOGLE_WEATHER_DAILY_LIMIT", "Weather:Worker:DailyLimit:GoogleWeather"),
        ("WEATHER_CANADA_DAILY_LIMIT", "Weather:Worker:DailyLimit:WeatherCanada"),
        ("WEATHER_STATE_DIR", "Weather:Lifecycle:StateDir"),
    ];

    /// <summary>
    /// Reads every known flat variable from <paramref name="source"/> (which already layers the
    /// <c>.env</c> file under the real process environment) and returns the section keys it populates.
    /// </summary>
    public static List<KeyValuePair<string, string?>> Resolve(IConfiguration source)
    {
        var resolved = new List<KeyValuePair<string, string?>>();

        foreach ((string variable, string key) in Aliases)
        {
            string? value = source[variable];
            if (!string.IsNullOrWhiteSpace(value))
            {
                resolved.Add(new KeyValuePair<string, string?>(key, value));
            }
        }

        return resolved;
    }
}
