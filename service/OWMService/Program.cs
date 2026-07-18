using OWMService.Config;
using OWMService.Logging;
using System;
using System.Configuration;
using System.Diagnostics;
using System.ServiceProcess;

namespace OWMService
{
    static class Program
    {
        private static IEventLogger s_logger;

        [STAThread]
        static void Main(string[] args)
        {
            Console.WriteLine(">>> OWMService.Main() started");

            try
            {
                // Initialize logger first
                string eventLogSource = ConfigurationManager.AppSettings["EventLogSource"] ?? "OWMService";
                string eventLogName = ConfigurationManager.AppSettings["EventLogName"] ?? "Application";
                string logFilePath = ConfigurationManager.AppSettings["LogFilePath"];

                Console.WriteLine($">>> Creating logger: source={eventLogSource}, logName={eventLogName}, path={logFilePath ?? "(default)"}");

                s_logger = LoggerFactory.CreateDefaultLogger(eventLogSource, eventLogName, 
                    string.IsNullOrEmpty(logFilePath) ? null : logFilePath);

                Console.WriteLine(">>> Logger created successfully");

                s_logger.LogInfo("========== OWMService Startup ==========");
                s_logger.LogInfo($"Debug Build: {IsDebugBuild()}");
                s_logger.LogInfo($"UserInteractive: {Environment.UserInteractive}");
                s_logger.LogInfo($"Debugger.IsAttached: {Debugger.IsAttached}");
                s_logger.LogInfo($"CurrentDirectory: {System.IO.Directory.GetCurrentDirectory()}");

                if (Environment.UserInteractive || Debugger.IsAttached)
                {
                    // Debug mode
                    RunDebugMode();
                }
                else
                {
                    // Service mode
                    RunServiceMode();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($">>> EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($">>> StackTrace: {ex.StackTrace}");

                if (s_logger != null)
                {
                    s_logger.LogError($"Fatal startup error: {ex.Message}\n{ex.StackTrace}");
                }
            }
            finally
            {
                Console.WriteLine(">>> OWMService.Main() finally block");
                s_logger?.LogInfo("========== OWMService Shutdown ==========");
                (s_logger as IDisposable)?.Dispose();
                Console.WriteLine(">>> OWMService.Main() completed");
            }
        }

        private static void RunDebugMode()
        {
            s_logger.LogInfo("Running in DEBUG mode (console)");
            Console.WriteLine("\n=== OWMService Debug Mode ===");
            Console.WriteLine($"UserInteractive: {Environment.UserInteractive}");
            Console.WriteLine($"Debugger.IsAttached: {Debugger.IsAttached}");
            Console.WriteLine($"Log directory: C:\\Users\\{Environment.UserName}\\AppData\\Roaming\\OWMService\\Logs");

            var service = new RWS(s_logger);
            service.StartDebug(null);

            Console.WriteLine("\nPress Enter to stop...");
            Console.ReadLine();

            service.StopDebug();
        }

        private static void RunServiceMode()
        {
            s_logger.LogInfo("Running as Windows Service");
            ServiceBase.Run(new ServiceBase[] { new RWS(s_logger) });
        }

        private static bool IsDebugBuild()
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
}