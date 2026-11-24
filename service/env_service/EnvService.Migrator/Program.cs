using EnvService.Migrator;

// EnvService.Migrator — one-off copy of the MSSQL water/weather data into MySQL envdata (env_service plan step 3).
//
//   EnvService.Migrator [--env-file <path>] [--days 30] [--steps stations,water,weather,fills] [--dry-run] [--force-weather] [--no-prompt]
//
// Connections, from real environment variables or --env-file (plain KEY=VALUE; enc:v1: values are NOT decrypted, use a
// plaintext file):
//   DB_URL / DB_USERNAME / DB_PASSWORD           SQL Server (jdbc:sqlserver:// or a SqlClient string)  - read only
//   MYSQL_URL / MYSQL_USERNAME / MYSQL_PASSWORD  envdata     (jdbc:mysql:// or a MySqlConnector string) - procedures only
// A missing DB_PASSWORD / MYSQL_PASSWORD is asked for on the console (not echoed); --no-prompt skips that (Windows
// authentication, or a local account without a password).
// Order: stations first (readings need them), then water, weather, fills. Run weather BEFORE weather 11.0.0 is
// deployed: it refuses when envdata already holds weather, unless --force-weather.
try
{
    Dictionary<string, string> cli = ParseArgs(args);
    Dictionary<string, string> env = new(StringComparer.OrdinalIgnoreCase);
    if (cli.TryGetValue("env-file", out string? envFile))
    {
        foreach (string line in File.ReadAllLines(envFile))
        {
            string t = line.Trim();
            int eq = t.IndexOf('=');
            if (t.Length == 0 || t[0] == '#' || eq <= 0)
            {
                continue;
            }
            env[t[..eq].Trim()] = t[(eq + 1)..].Trim();
        }
    }

    string? Get(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
        : env.TryGetValue(name, out string? f) && f.Length > 0 ? f : null;

    // A password is taken only from where its user name came from: a real variable MYSQL_USERNAME=wbadmin must never be
    // paired with the file's MYSQL_PASSWORD of another account (then the password is asked for instead).
    string? Password(string user, string password)
    {
        bool userFromEnv = Environment.GetEnvironmentVariable(user) is { Length: > 0 };
        return userFromEnv
            ? Environment.GetEnvironmentVariable(password) is { Length: > 0 } p ? p : null
            : Get(password);
    }

    foreach (string name in new[] { "DB_URL", "DB_PASSWORD", "MYSQL_URL", "MYSQL_PASSWORD" })
    {
        if (Get(name) is { } value && value.StartsWith("enc:v1:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{name} is enc:v1: ciphertext; give a plaintext env file or real variables");
        }
    }

    bool noPrompt = cli.ContainsKey("no-prompt");
    string mssqlUrl = Get("DB_URL") ?? throw new InvalidOperationException("DB_URL (SQL Server) is not set");
    string mysqlUrl = Get("MYSQL_URL") ?? throw new InvalidOperationException("MYSQL_URL (envdata) is not set");
    string mssql = SqlServerUrl.Build(mssqlUrl, Get("DB_USERNAME"), Password("DB_USERNAME", "DB_PASSWORD") ?? (noPrompt ? null : Ask("SQL Server password")));
    string mysql = MySqlUrl.Build(mysqlUrl, Get("MYSQL_USERNAME"), Password("MYSQL_USERNAME", "MYSQL_PASSWORD") ?? (noPrompt ? null : Ask("MySQL password")));

    int days = cli.TryGetValue("days", out string? d) ? int.Parse(d) : 30;
    if (days is < 1 or > 60)
    {
        throw new ArgumentException("--days must be 1..60");
    }
    var steps = new HashSet<string>(
        (cli.TryGetValue("steps", out string? s) ? s : "stations,water,weather,fills")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase);
    foreach (string step in steps)
    {
        if (step is not ("stations" or "water" or "weather" or "fills"))
        {
            throw new ArgumentException($"unknown step '{step}' (stations, water, weather, fills)");
        }
    }

    var options = new MigratorOptions(days, steps, cli.ContainsKey("dry-run"), cli.ContainsKey("force-weather"));
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    return await new Migrator(mssql, mysql, options, Console.Out).RunAsync(cts.Token);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Console.Error.WriteLine("EnvService.Migrator FAILED: " + ex.Message);
    return 1;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("unexpected argument " + args[i]);
        }
        string key = args[i][2..];
        bool flag = key is "dry-run" or "force-weather" or "no-prompt";
        result[key] = flag ? "true" : (i + 1 < args.Length ? args[++i] : throw new ArgumentException("--" + key + " needs a value"));
    }
    return result;
}

static string Ask(string what)
{
    Console.Error.Write(what + ": ");
    var chars = new System.Text.StringBuilder();
    while (true)
    {
        ConsoleKeyInfo k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter)
        {
            break;
        }
        if (k.Key == ConsoleKey.Backspace)
        {
            if (chars.Length > 0)
            {
                chars.Length--;
            }
            continue;
        }
        chars.Append(k.KeyChar);
    }
    Console.Error.WriteLine();
    return chars.ToString();
}
