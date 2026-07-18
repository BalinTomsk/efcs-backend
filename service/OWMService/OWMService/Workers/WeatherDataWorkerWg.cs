namespace OWMService.Workers
{
    using OWMService.Config;
    using OWMService.Logging;

    public class WeatherDataWorkerWg : WeatherDataWorkerBase
    {
        public WeatherDataWorkerWg(IEventLogger logger) : base(logger)
        {
        }
        public override string GetServiceName() { return "wunderground"; }

        public override string GetStationQuery()
        {
            return $"select TOP {GetStationMaxLimitPerDay()} mli, lat, lon, state from dbo.vwWeatherForecastToDay WHERE country = 'CA'";
        }
        protected override int GetStationMaxLimitPerDay() {  return 1000; }

        /// <summary>
        ///   Weather Underground  https://www.wunderground.com/
        ///   https://www.wunderground.com/member/api-keys
        /// </summary>
        protected override string GetApiUrl(float lat, float lon, Settings settings)
        {
            return $"https://api.weather.com/v3/wx/forecast/daily/5day?geocode={lat},{lon}&format=json&units=e&language=en-US&apiKey={settings.Wunderground}";
        }

        protected override int GetSourceType()
        {
            return 1;
        }
    }

    public class StationData
    {
        public string Mli { get; set; }
        public float Latitude { get; set; }
        public float Longitude { get; set; }
        public string State { get; set; }
    }
}