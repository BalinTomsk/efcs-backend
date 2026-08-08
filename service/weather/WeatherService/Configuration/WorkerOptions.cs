namespace WeatherService.Configuration;

/// <summary>
/// Configurable worker behaviour, bound from the <c>Weather:Worker</c> configuration section (see
/// <c>appsettings.json</c>). Mirrors the Spring <c>weather.worker.*</c> properties one-for-one.
/// </summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Weather:Worker";

    /// <summary>TCP connect-establishment timeout for the upstream provider APIs.</summary>
    public int ConnectTimeoutMs { get; set; } = 15000;

    /// <summary>Response read timeout for the upstream provider APIs.</summary>
    public int ReadTimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Hard cap on a stored payload. A body larger than this is rejected rather than persisted — the
    /// response is written to the database verbatim, so an unbounded read is an unbounded INSERT.
    /// </summary>
    public int MaxResponseBytes { get; set; } = 5_242_880;

    /// <summary>
    /// Fetch one known-good station per provider at startup and log the outcome, before the daily loop
    /// begins. Gives a deploy an immediate pass/fail signal per provider.
    /// </summary>
    public bool StartupVerificationEnabled { get; set; } = true;

    public string OpenMeteoBaseUrl { get; set; } = "https://api.open-meteo.com/v1/forecast";

    public string WeatherGovBaseUrl { get; set; } = "https://api.weather.gov";

    /// <summary>Weather.gov requires an identifying User-Agent naming a reachable contact.</summary>
    public string WeatherGovUserAgent { get; set; } = "efj-backend-weather/1.0 (ops@fishfind.com)";

    public string WeatherCanadaBaseUrl { get; set; } = "https://api.weather.gc.ca";

    public string WeatherCanadaUserAgent { get; set; } = "efj-backend-weather/1.0 (ops@fishfind.com)";

    /// <summary>Half-width, in degrees, of the bounding box searched for the nearest SWOB observation.</summary>
    public double WeatherCanadaBboxRadiusDegrees { get; set; } = 0.5;

    public string VisualCrossingBaseUrl { get; set; } =
        "https://weather.visualcrossing.com/VisualCrossingWebServices/rest/services/timeline";

    /// <summary>
    /// Blank disables the Visual Crossing worker entirely — <c>StationWorker</c> does not start it (see
    /// <c>StationWorker.MissingConfigurationFor</c>). The fetcher also refuses to call without a key, as
    /// defence in depth for any path that reaches it directly.
    /// </summary>
    public string VisualCrossingApiKey { get; set; } = string.Empty;

    public string GoogleWeatherBaseUrl { get; set; } = "https://weather.googleapis.com/v1/currentConditions:lookup";

    /// <summary>Blank disables the Google Weather worker entirely, like <see cref="VisualCrossingApiKey"/>.</summary>
    public string GoogleWeatherApiKey { get; set; } = string.Empty;

    public ProviderToggleOptions Enable { get; set; } = new();

    public ProviderTimeoutOptions Timeout { get; set; } = new();

    public RateLimitOptions RateLimit { get; set; } = new();

    public DailyLimitOptions DailyLimit { get; set; } = new();

    public PostProcessingOptions PostProcessing { get; set; } = new();

    /// <summary>
    /// Per-provider on/off switch. All default to <c>true</c>: a provider is opted <em>out</em>
    /// explicitly, so adding a new one never requires touching every deployment's env file.
    ///
    /// <para>A disabled worker is not started at all — see <c>StationWorker.WorkerDisabledReason</c>.
    /// This is the knob for taking one provider out of rotation (quota exhausted, upstream incident,
    /// key revoked) without redeploying a different image or touching the other four.</para>
    /// </summary>
    public sealed class ProviderToggleOptions
    {
        public bool WeatherGov { get; set; } = true;

        public bool OpenMeteo { get; set; } = true;

        public bool VisualCrossing { get; set; } = true;

        public bool GoogleWeather { get; set; } = true;

        public bool WeatherCanada { get; set; } = true;
    }

    /// <summary>
    /// Seconds to wait between calls to each provider — the pacing that keeps a cycle from hammering a
    /// public or metered API.
    ///
    /// <para><strong>0 (the default) means "derive it"</strong>: that provider's
    /// <see cref="DailyLimitOptions"/> spread evenly over
    /// <see cref="StationWorker.DerivedPacingWindow"/> (12 hours), so a full day's allowance is consumed
    /// over half a day and the cycle still finishes with room to spare. Set a value only to override
    /// that — an explicit number is used verbatim, including one below the two-second floor that guards
    /// the derived path.</para>
    /// </summary>
    public sealed class ProviderTimeoutOptions
    {
        public int WeatherGov { get; set; }

        public int OpenMeteo { get; set; }

        public int VisualCrossing { get; set; }

        public int GoogleWeather { get; set; }

        public int WeatherCanada { get; set; }
    }

    /// <summary>Inline handling of an upstream HTTP 429, before any retry strategy sees the failure.</summary>
    public sealed class RateLimitOptions
    {
        /// <summary>How many times a <c>Retry-After</c> wait is honoured before giving up on the station.</summary>
        public int MaxRetries { get; set; } = 2;

        /// <summary>Wait used when the 429 carries no usable <c>Retry-After</c> header.</summary>
        public long DefaultWaitMs { get; set; } = 5000;

        /// <summary>Upper clamp on any honoured wait, so a hostile header cannot stall the pass.</summary>
        public long MaxWaitMs { get; set; } = 60000;
    }

    /// <summary>
    /// Per-provider cap on stations attempted per UTC day, enforced across restarts by
    /// <c>WeatherApiUsageTracker</c>. These are the paid/quota'd limits, not a pacing knob.
    /// </summary>
    public sealed class DailyLimitOptions
    {
        public int WeatherGov { get; set; } = 900;

        public int OpenMeteo { get; set; } = 1400;

        public int VisualCrossing { get; set; } = 900;

        public int GoogleWeather { get; set; } = 161;

        public int WeatherCanada { get; set; } = 900;
    }

    public sealed class PostProcessingOptions
    {
        /// <summary>
        /// Fraction of attempted stations that may fail before post-processing is skipped for the cycle.
        /// </summary>
        public double MaxFailureRate { get; set; } = 0.5;
    }
}
