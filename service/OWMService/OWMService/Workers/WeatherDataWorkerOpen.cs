namespace OWMService.Workers
{
    using OWMService.Config;
    using OWMService.Logging;

    /// <summary>
    /// Worker implementation responsible for fetching weather forecast data 
    /// from the Open-Meteo API (https://open-meteo.com/).
    /// </summary>
    public class WeatherDataWorkerOpen : WeatherDataWorkerBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="WeatherDataWorkerOpen"/> class.
        /// </summary>
        /// <param name="logger">The logger used for recording service events and errors.</param>
        public WeatherDataWorkerOpen(IEventLogger logger) : base(logger)
        {
        }

        /// <summary>
        /// Gets the SQL query used to select which weather stations need processing.
        /// </summary>
        /// <returns>A SQL command string for selecting Open-Meteo specific stations.</returns>
        public override string GetStationQuery()
        {
            return $"select TOP {GetStationMaxLimitPerDay()} mli, lat, lon, state from dbo.vwWeatherForecastToDay WHERE country = 'US'";
        }

        /// <summary>
        /// Gets the identifying name of this specific weather worker service.
        /// </summary>
        /// <returns>The string "open-meteo".</returns>
        public override string GetServiceName() { return "open-meteo"; }

        /// <summary>
        /// Gets the maximum allowed number of stations to poll per day from this API provider.
        /// </summary>
        /// <returns>The daily station limit as an integer.</returns>
        protected override int GetStationMaxLimitPerDay() { return 1400; }

        /// <summary>
        /// Constructs the Open-Meteo API URL for the specified geographic coordinates.
        /// Example: https://api.open-meteo.com/v1/forecast?latitude=43.45&longitude=-80.49...
        /// </summary>
        /// <param name="lat">The latitude of the weather station.</param>
        /// <param name="lon">The longitude of the weather station.</param>
        /// <param name="settings">The application settings.</param>
        /// <returns>A fully qualified URL string for the Open-Meteo API request.</returns>
        protected override string GetApiUrl(float lat, float lon, Settings settings)
        {
            return $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}"
                + "&hourly=temperature_2m,relative_humidity_2m,precipitation_probability,pressure_msl,"
                + "wind_speed_10m,wind_direction_10m,weather_code,rain&daily=temperature_2m_max,temperature_2m_min&timezone=auto";
        }

        /// <summary>
        /// Gets the weather source type identifier mapped in the ows_meteo database table.
        /// </summary>
        /// <returns>The integer value representing the Open-Meteo source type (2).</returns>
        protected override int GetSourceType()
        {
            return 2;
        }
    }
}