namespace OWMService.Logging
{
    using System;
    using System.Diagnostics;
    using System.IO;

    public class FileEventLogger : IEventLogger, IDisposable
    {
        private readonly string m_logFilePath;
        private readonly EventLog m_eventLog;
        private bool m_disposed;

        public FileEventLogger(string source, string logName, string logFilePath = null)
        {
            string resolvedPath;

            if (!string.IsNullOrWhiteSpace(logFilePath))
            {
                resolvedPath = Environment.ExpandEnvironmentVariables(logFilePath);
            }
            else
            {
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                resolvedPath = Path.Combine(programData, "OWMService", "Logs", "OWMService.log");
            }

            m_logFilePath = resolvedPath;
            string logDir = Path.GetDirectoryName(m_logFilePath);

            try
            {
                if (!string.IsNullOrWhiteSpace(logDir) && !Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                File.AppendAllText(m_logFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INIT] Logger initialized{Environment.NewLine}");
            }
            catch
            {
            }

            try
            {
                if (!EventLog.SourceExists(source))
                {
                    EventLog.CreateEventSource(source, logName);
                }

                m_eventLog = new EventLog { Source = source, Log = logName };
            }
            catch
            {
            }
        }

        public void LogInfo(string message) => Log(message, "INFO");
        public void LogError(string message) => Log(message, "ERROR");
        public void LogWarning(string message) => Log(message, "WARN");
        public void LogDebug(string message) => Log(message, "DEBUG");

        private void Log(string message, string level)
        {
            try
            {
                File.AppendAllText(m_logFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (!m_disposed)
            {
                m_eventLog?.Dispose();
                m_disposed = true;
            }
        }
    }
}