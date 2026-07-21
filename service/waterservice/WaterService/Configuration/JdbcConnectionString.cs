using Microsoft.Data.SqlClient;

namespace WaterService.Configuration;

/// <summary>
/// Converts the shared JDBC-style <c>DB_URL</c> used across the FishFind backend services into a
/// Microsoft SqlClient connection string, so the same <c>.env</c> works for the Node, Java, and this
/// .NET service.
///
/// <para>Example input:
/// <c>jdbc:sqlserver://testserver.example.com:1433;databaseName=DB_x;encrypt=true;trustServerCertificate=true</c></para>
///
/// <para>If <c>DB_URL</c> is already a native SqlClient connection string (contains <c>Server=</c> or
/// <c>Data Source=</c>), it is used as-is with credentials merged in.</para>
/// </summary>
public static class JdbcConnectionString
{
    /// <summary>
    /// Builds a SqlClient connection string from a JDBC URL (or native connection string) plus credentials.
    /// </summary>
    /// <param name="dbUrl">JDBC URL or native SqlClient connection string.</param>
    /// <param name="username">SQL login (ignored when the URL already carries a User ID).</param>
    /// <param name="password">SQL password (ignored when the URL already carries a Password).</param>
    /// <returns>A SqlClient connection string.</returns>
    public static string Build(string dbUrl, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(dbUrl))
        {
            throw new ArgumentException("DB_URL must not be null or blank.", nameof(dbUrl));
        }

        var builder = LooksLikeJdbc(dbUrl)
            ? FromJdbc(dbUrl.Trim())
            : new SqlConnectionStringBuilder(dbUrl);

        if (!string.IsNullOrWhiteSpace(username) && string.IsNullOrEmpty(builder.UserID))
        {
            builder.UserID = username;
        }
        if (!string.IsNullOrWhiteSpace(password) && string.IsNullOrEmpty(builder.Password))
        {
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    private static bool LooksLikeJdbc(string value) =>
        value.StartsWith("jdbc:", StringComparison.OrdinalIgnoreCase);

    private static SqlConnectionStringBuilder FromJdbc(string jdbcUrl)
    {
        // jdbc:sqlserver://HOST:PORT;key=value;key=value
        const string prefix = "jdbc:sqlserver://";
        if (!jdbcUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Only jdbc:sqlserver URLs are supported for DB_URL: " + jdbcUrl, nameof(jdbcUrl));
        }

        string remainder = jdbcUrl.Substring(prefix.Length);
        string[] parts = remainder.Split(';');
        string hostPort = parts[0].Trim();

        var builder = new SqlConnectionStringBuilder
        {
            // SqlClient wants "host,port" (comma), not "host:port" (colon).
            DataSource = ToDataSource(hostPort),
            // Safe defaults; overridden below if the URL specifies them.
            Encrypt = true,
            TrustServerCertificate = true,
        };

        for (int i = 1; i < parts.Length; i++)
        {
            string segment = parts[i].Trim();
            if (segment.Length == 0)
            {
                continue;
            }

            int eq = segment.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = segment.Substring(0, eq).Trim();
            string val = segment.Substring(eq + 1).Trim();

            switch (key.ToLowerInvariant())
            {
                case "databasename":
                case "database":
                    builder.InitialCatalog = val;
                    break;
                case "encrypt":
                    builder.Encrypt = ParseBool(val);
                    break;
                case "trustservercertificate":
                    builder.TrustServerCertificate = ParseBool(val);
                    break;
                case "user":
                case "username":
                    builder.UserID = val;
                    break;
                case "password":
                    builder.Password = val;
                    break;
                case "logintimeout":
                    if (int.TryParse(val, out int lt))
                    {
                        builder.ConnectTimeout = lt;
                    }
                    break;
                // Other JDBC-only properties (applicationName, etc.) are ignored.
            }
        }

        return builder;
    }

    private static string ToDataSource(string hostPort)
    {
        int colon = hostPort.LastIndexOf(':');
        if (colon > 0 && colon < hostPort.Length - 1)
        {
            string host = hostPort.Substring(0, colon);
            string port = hostPort.Substring(colon + 1);
            return host + "," + port;
        }
        return hostPort;
    }

    private static bool ParseBool(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
}
