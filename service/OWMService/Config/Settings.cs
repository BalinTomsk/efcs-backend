using System;
using System.Data.SqlClient;

namespace OWMService.Config
{
    public class Settings
    {
        // Defaults preserved from original RWS fields
        public string Server { get; set; } = Environment.MachineName;
        public string DbName { get; set; } = "fishfind";
        public string UserName { get; set; } = "superadmin";
        public string UserPassword { get; set; } = "superpassword";
        public string Wunderground { get; set; } = "weather APi Key"; // https://preview.wunderground.com/member/api-keys
        public int Interval { get; set; }

        public string GetConnectionString()
        {
            if (string.IsNullOrWhiteSpace(Server) || string.IsNullOrWhiteSpace(DbName))
            {
                return string.Empty;
            }

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = Server,
                InitialCatalog = DbName,
                IntegratedSecurity = false,
                UserID = UserName ?? string.Empty,
                Password = UserPassword ?? string.Empty
            };

            return builder.ConnectionString;
        }
    }
}