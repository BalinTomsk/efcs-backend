namespace WaterService.Configuration;

/// <summary>
/// Loads a local <c>.env</c> file and exposes its values as a <strong>lowest-precedence</strong>
/// configuration source, mirroring the Java <c>DotenvEnvironmentPostProcessor</c>.
///
/// <para>Real OS environment variables and other configuration always win, so production deployments
/// that inject <c>DB_URL</c>/<c>DB_USERNAME</c>/<c>DB_PASSWORD</c> as environment variables are
/// unaffected — the <c>.env</c> file is only a local development fallback. Only keys declared in the
/// file are imported; the whole OS environment is never copied.</para>
/// </summary>
public static class DotEnvLoader
{
    private const string DotenvPathEnv = "DOTENV_PATH";
    private const string DefaultDotenvFile = ".env";

    /// <summary>
    /// Reads the resolved <c>.env</c> file and returns its declared, non-blank entries.
    /// Never throws for a missing or malformed file.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Load()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? path = ResolvePath();
        if (path is null || !File.Exists(path))
        {
            return values;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception)
        {
            // The file exists but is unreadable (e.g. wrong ownership on the mounted secret) or locked.
            // Never crash the app for this — real environment variables may still supply configuration.
            return values;
        }

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1).Trim();

            // Strip optional surrounding quotes.
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value.Substring(1, value.Length - 2);
            }

            if (key.Length > 0 && value.Length > 0)
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static string? ResolvePath()
    {
        string? configured = Environment.GetEnvironmentVariable(DotenvPathEnv);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        string cwdDotenv = Path.Combine(Directory.GetCurrentDirectory(), DefaultDotenvFile);
        return File.Exists(cwdDotenv) ? cwdDotenv : null;
    }
}
