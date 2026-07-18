using OWMService.Config;

namespace OWMService.Workers
{
    public interface IWeatherDataWorker
    {
        /// <summary>
        /// Processes weather data for all stations within the given time budget.
        /// </summary>
        /// <param name="settings">Application settings.</param>
        /// <param name="timeBudget">Total time budget to spread station processing across.</param>
        /// <returns>True if completed successfully, false on auth failure or error.</returns>
        bool Process(Settings settings, System.TimeSpan timeBudget);
    }
}
