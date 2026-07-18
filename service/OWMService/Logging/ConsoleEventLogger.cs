using System;

namespace OWMService.Logging
{
    // Simple console logger used in Debug configuration.
    public class ConsoleEventLogger : IEventLogger, IDisposable
    {
        private bool _disposed;

        public void LogInfo(string message)
        {
            Console.WriteLine("[INFO] " + message);
        }

        public void LogError(string message)
        {
            Console.WriteLine("[ERROR] " + message);
        }

        public void LogWarning(string message)
        {
            Console.WriteLine("[WARN] " + message);
        }

        public void LogDebug(string message)
        {
            Console.WriteLine("[DEBUG] " + message);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                // nothing to dispose for console logger, but keep pattern consistent
                _disposed = true;
            }
        }
    }
}