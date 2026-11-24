using MySqlConnector;

namespace EnvService.Configuration;

/// <summary>
/// Builds the MySqlConnector connection string for the <c>envdata</c> database from <c>MYSQL_URL</c> /
/// <c>MYSQL_USERNAME</c> / <c>MYSQL_PASSWORD</c>. <c>MYSQL_URL</c> is either a JDBC URL
/// (<c>jdbc:mysql://HOST:PORT/envdata?sslMode=REQUIRED</c>, the form the Java services use) or a native
/// MySqlConnector connection string. Credentials in the URL win over the separate variables.
/// </summary>
public static class MySqlUrl
{
    public static string Build(string url, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("MYSQL_URL must not be null or blank.", nameof(url));
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

        // A pooled session must never carry state (user variables, temp tables) from one call to the next.
        builder.ConnectionReset = true;

        // OUT parameters are read as session variables (CALL p(..., @x); SELECT @x): the EXECUTE-only account may not
        // read procedure metadata, which a StoredProcedure-mode OUT parameter needs. Same as csnews-env.
        builder.AllowUserVariables = true;

        // envdata has no GUID columns this service reads, but a CHAR(36) must never turn into a Guid silently.
        builder.GuidFormat = MySqlGuidFormat.None;

        // sp_clean_old_water_data may reorganise a partition; give it room, but never hang a cycle.
        builder.DefaultCommandTimeout = 120;
        return builder.ConnectionString;
    }

    private static MySqlConnectionStringBuilder FromJdbc(string jdbcUrl)
    {
        const string prefix = "jdbc:mysql://";
        if (!jdbcUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only jdbc:mysql URLs are supported for MYSQL_URL: " + jdbcUrl, nameof(jdbcUrl));
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
            // envdata's accounts are REQUIRE SSL: encrypt by default, the URL may ask for verification.
            SslMode = MySqlSslMode.Required,
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
                // Connector/J's useSSL=true encrypts without verifying the certificate: MySqlConnector's Required.
                case "usessl":
                    builder.SslMode = IsTrue(val) ? MySqlSslMode.Required : MySqlSslMode.None;
                    break;
                case "sslmode":
                    builder.SslMode = val.ToUpperInvariant() switch
                    {
                        "DISABLED" => MySqlSslMode.None,
                        "PREFERRED" => MySqlSslMode.Preferred,
                        "VERIFY_CA" => MySqlSslMode.VerifyCA,
                        "VERIFY_IDENTITY" => MySqlSslMode.VerifyFull,
                        _ => MySqlSslMode.Required,
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
                // serverTimezone and the other Connector/J-only properties are ignored: every envdata time is UTC.
            }
        }

        return builder;
    }

    private static bool IsTrue(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
}
