using System;
using System.Diagnostics;

namespace OWMService.Logging
{
    public class EventLogLogger : IEventLogger, IDisposable
    {
        private readonly EventLog _eventLog;
        private bool _disposed;

        public EventLogLogger(string source, string logName)
        {
            try
            {
                if (!EventLog.SourceExists(source))
                {
                    EventLog.CreateEventSource(source, logName);
                }

                _eventLog = new EventLog
                {
                    Source = source,
                    Log = logName
                };
            }
            catch (Exception ex)
            {
                // If Event Log cannot be initialized (permissions, etc.), fall back to console only.
                Console.WriteLine("Failed to initialize Windows Event Log: " + ex.Message);
                _eventLog = null;
            }
        }

        public void LogInfo(string message)
        {
            WriteEntry(message, EventLogEntryType.Information);
        }

        public void LogError(string message)
        {
            WriteEntry(message, EventLogEntryType.Error);
        }

        public void LogWarning(string message)
        {
            WriteEntry(message, EventLogEntryType.Warning);
        }

        public void LogDebug(string message)
        {
            // Windows Event Log doesn't have a Debug level; prefix and use Information.
            WriteEntry("[DEBUG] " + message, EventLogEntryType.Information);
        }

        private void WriteEntry(string message, EventLogEntryType entryType)
        {
            Console.WriteLine(message);

            if (_eventLog == null)
            {
                return;
            }

            try
            {
                _eventLog.WriteEntry(message, entryType);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to write to Windows Event Log: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _eventLog?.Dispose();
                _disposed = true;
            }
        }
    }
}