using Microsoft.Win32;
using System;

namespace OWMService.Config
{
    public class RegistrySettingsProvider : ISettingsProvider
    {
        private const string SubKey = @"SOFTWARE\FishFind\OWMService";

        public bool TryReadSettings(out Settings settings, out string errorMessage)
        {
            settings = null;
            errorMessage = null;

            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(SubKey);

                if (key == null)
                {
                    errorMessage = "Cannot open registry key: HKLM\\" + SubKey;
                    return false;
                }

                var server = key.GetValue("Server") as string;
                if (string.IsNullOrWhiteSpace(server))
                {
                    errorMessage = "Cannot read MSSQL Server Name from registry.";
                    return false;
                }

                var dbName = key.GetValue("dbName") as string;
                if (string.IsNullOrWhiteSpace(dbName))
                {
                    errorMessage = "Cannot read MSSQL Server Db Name from registry.";
                    return false;
                }

                var userName = key.GetValue("userName") as string;
                var userPassword = key.GetValue("userPassword") as string;
                var wunderground = key.GetValue("wunderground") as string;

                int interval = 0;
                object intervalValue = key.GetValue("Interval");
                if (intervalValue != null)
                {
                    try
                    {
                        interval = Convert.ToInt32(intervalValue);
                    }
                    catch
                    {
                        // ignore - keep default 0
                    }
                }

                settings = new Settings
                {
                    Server = server,
                    DbName = dbName,
                    UserName = userName,
                    UserPassword = userPassword,
                    Wunderground = wunderground,
                    Interval = interval
                };

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = "ReadSettings failed: " + ex.Message;
                return false;
            }
        }
    }
}