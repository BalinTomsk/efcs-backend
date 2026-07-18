using System;

namespace OWMService.Logging
{
    public static class LoggerFactory
    {
        /// <summary>
        /// Creates a logger that writes all logs to file and errors to Windows Event Log.
        /// </summary>
        /// <param name="source">Event Log source name</param>
        /// <param name="logName">Event Log name</param>
        /// <param name="logFilePath">Optional custom log file path. If null, uses AppData\OWMService\Logs\OWMService.log</param>
        /// <returns>Configured file logger</returns>
        public static IEventLogger CreateDefaultLogger(string source, string logName, string logFilePath = null)
        {
#if DEBUG
            // In debug builds, use file logger (writes all logs to file + errors to Event Log)
            return new FileEventLogger(source, logName, logFilePath);
#else
            // In release builds, also use file logger for consistency
            return new FileEventLogger(source, logName, logFilePath);
#endif
        }

        /// <summary>
        /// Creates a console-only logger for testing or special scenarios.
        /// </summary>
        public static IEventLogger CreateConsoleLogger()
        {
            return new ConsoleEventLogger();
        }
    }
}