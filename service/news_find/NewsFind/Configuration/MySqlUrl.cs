using MySqlConnector;

namespace NewsFind.Configuration;

/// <summary>
/// Converts docapi's JDBC-style <c>MYSQL_NEWS_URL</c> into a MySqlConnector connection string, so the same
/// three keys (<c>MYSQL_NEWS_URL</c> / <c>_USERNAME</c> / <c>_PASSWORD</c>) work for both services.
///
/// <para>Example input: <c>jdbc:mysql://mysql.example.com:3306/envfish?useSSL=true&amp;serverTimezone=UTC</c>.
/// A native connection string (contains <c>Server=</c>) is used as is, with credentials merged in.</para>
/// </summary>
public static class MySqlUrl
{
    public static string Build(string url, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("MYSQL_NEWS_URL must not be null or blank.", nameof(url));
        }

        MySqlConnectionStringBuilder builder = url.Trim().StartsWith("jdbc:", StringComparison.OrdinalIgnoreCase)
            ? FromJdbc(url.Trim())
            : new MySqlConnectionStringBuilder(url);

        if (!string.IsNullOrWhiteSpace(username) && string.IsNullOrEmpty(builder.UserID))
        {
            builder.UserID = username;
        }
        if (!string.IsNullOrWhiteSpace(password) && string.IsNullOrEmpty(builder.Password))
        {
            builder.Password = password;
        }

        // Read-only service: never let a pooled session carry state between callers.
        builder.ConnectionReset = true;

        // MySqlConnector's default turns every CHAR(36) column (news_id, lake_id, fish*_id) into a Guid, and
        // GetString then throws InvalidCastException — the first full sync on vector failed that way.
        builder.GuidFormat = MySqlGuidFormat.None;
        return builder.ConnectionString;
    }

    private static MySqlConnectionStringBuilder FromJdbc(string jdbcUrl)
    {
        const string prefix = "jdbc:mysql://";
        if (!jdbcUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only jdbc:mysql URLs are supported for MYSQL_NEWS_URL.", nameof(jdbcUrl));
        }

        string rest = jdbcUrl[prefix.Length..];
        string query = string.Empty;
        int q = rest.IndexOf('?');
        if (q >= 0)
        {
            query = rest[(q + 1)..];
            rest = rest[..q];
        }

        int slash = rest.IndexOf('/');
        string hostPort = slash >= 0 ? rest[..slash] : rest;
        string database = slash >= 0 ? rest[(slash + 1)..] : string.Empty;

        var builder = new MySqlConnectionStringBuilder
        {
            Database = Uri.UnescapeDataString(database),
            SslMode = MySqlSslMode.Preferred,
            ConnectionTimeout = 15,
        };

        int colon = hostPort.LastIndexOf(':');
        if (colon > 0 && uint.TryParse(hostPort[(colon + 1)..], out uint port))
        {
            builder.Server = hostPort[..colon];
            builder.Port = port;
        }
        else
        {
            builder.Server = hostPort;
        }

        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = pair[..eq].Trim().ToLowerInvariant();
            string val = Uri.UnescapeDataString(pair[(eq + 1)..].Trim());
            switch (key)
            {
                // Connector/J's useSSL=true encrypts without verifying the certificate — MySqlConnector's Required.
                case "usessl":
                    builder.SslMode = IsTrue(val) ? MySqlSslMode.Required : MySqlSslMode.None;
                    break;
                case "sslmode":
                    builder.SslMode = val.ToUpperInvariant() switch
                    {
                        "DISABLED" => MySqlSslMode.None,
                        "REQUIRED" => MySqlSslMode.Required,
                        "VERIFY_CA" => MySqlSslMode.VerifyCA,
                        "VERIFY_IDENTITY" => MySqlSslMode.VerifyFull,
                        _ => MySqlSslMode.Preferred,
                    };
                    break;
                case "user":
                    builder.UserID = val;
                    break;
                case "password":
                    builder.Password = val;
                    break;
                case "connecttimeout":
                    // Connector/J gives milliseconds.
                    if (int.TryParse(val, out int ms) && ms > 0)
                    {
                        builder.ConnectionTimeout = (uint)Math.Max(1, ms / 1000);
                    }
                    break;
                // serverTimezone and the other Connector/J-only properties are ignored: news_stamp is read
                // as a plain DATETIME and treated as UTC.
            }
        }

        return builder;
    }

    private static bool IsTrue(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
}
