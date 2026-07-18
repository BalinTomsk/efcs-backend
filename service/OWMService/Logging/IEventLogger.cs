using System.Diagnostics;

namespace OWMService.Logging
{
    public interface IEventLogger
    {
        void LogInfo(string message);
        void LogError(string message);
        void LogWarning(string message);
        void LogDebug(string message);
    }
}