using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using WeatherService.Configuration;
using WeatherService.Processing;
using WeatherService.Reporting;
using WeatherService.Web;

// Load a local .env file as the LOWEST-precedence configuration source, so real environment variables
// and appsettings always win (production injects DB_URL/DB_USERNAME/DB_PASSWORD as env vars).
// enc:v1: values in the file are decrypted here (see SecretCodec).
List<KeyValuePair<string, string?>> dotenv = DotEnvLoader.Load()
    .Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value))
    .ToList();

// Credentials do not always arrive through the .env file: a container run with Docker's --env-file has
// them injected as real environment variables the loader above never sees. Decrypt those separately.
// Empty (and therefore a complete no-op) for the existing all-plaintext deployments.
List<KeyValuePair<string, string?>> decryptedEnv = SecretCodec.DecryptEnvironmentVariables()
    .Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value))
    .ToList();

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateBootstrapLogger();

bool consoleMode = args.Contains("--console");
string? stationFilter = args
    .FirstOrDefault(a => a.StartsWith("--station=", StringComparison.Ordinal))
    ?.Substring("--station=".Length);

try
{
    return consoleMode
        ? await RunConsoleAsync(args, dotenv, decryptedEnv, stationFilter)
        : await RunWebAsync(args, dotenv, decryptedEnv);
}
catch (Exception ex)
{
    Log.Fatal(ex, "weather-station-pusher terminated unexpectedly.");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

// ---------------------------------------------------------------------------------------------------

// Normal mode: run the five provider loops and expose the health endpoints on 8081.
static async Task<int> RunWebAsync(
    string[] args, List<KeyValuePair<string, string?>> dotenv, List<KeyValuePair<string, string?>> decryptedEnv)
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    ConfigureSources(builder.Configuration, dotenv, decryptedEnv);
    builder.Services.AddSerilog(ConfigureSerilog);

    // Single listener. The app has no controllers, so this port carries only the health endpoints —
    // matching the Java service, which likewise put actuator on 8081 and nothing on 8080.
    builder.WebHost.UseUrls("http://0.0.0.0:8081");

    // Give the worker loops and the lifecycle tracker time to unwind on SIGTERM
    // (spring: lifecycle.timeout-per-shutdown-phase: 30s).
    builder.Services.Configure<HostOptions>(options =>
        options.ShutdownTimeout = TimeSpan.FromSeconds(30));

    builder.Services.AddWeatherServices(builder.Configuration);
    builder.Services.AddHealthChecks().AddCheck<DbHealthCheck>("db", tags: ["ready"]);

    // ORDER MATTERS: hosted services stop in reverse registration order, so the lifecycle tracker
    // registered first is the last to stop — its "clean shutdown" marker is written only after the
    // workers have actually finished, which is what makes crash detection trustworthy.
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ServiceLifecycleTracker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<StationWorker>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<WeeklyReportMailService>());

    WebApplication app = builder.Build();

    // Paths mirror Spring Actuator's, so the Docker HEALTHCHECK and any external monitoring are
    // identical across the two implementations.
    app.MapGet("/actuator/health", () => Results.Json(new
    {
        status = "UP",
        version = AppInfo.Version,
        uptime = AppInfo.UptimeSeconds,
    }));

    // Liveness is process-only (never DB-dependent), so a DB blip does not restart the container —
    // the worker sleeps and recovers on its own. Readiness reflects datasource connectivity.
    app.MapHealthChecks("/actuator/health/liveness", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/actuator/health/readiness",
        new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

    await app.RunAsync();
    return 0;
}

// Console mode (--console [--station=<MLI>]): run exactly one US pass, then exit.
static async Task<int> RunConsoleAsync(
    string[] args,
    List<KeyValuePair<string, string?>> dotenv,
    List<KeyValuePair<string, string?>> decryptedEnv,
    string? station)
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
    ConfigureSources(builder.Configuration, dotenv, decryptedEnv);
    builder.Services.AddSerilog(ConfigureSerilog);
    builder.Services.AddWeatherServices(builder.Configuration);

    using IHost host = builder.Build();

    StationWorker worker = host.Services.GetRequiredService<StationWorker>();
    Log.Information("Running console debug mode. country=US station={Station}", station ?? "<all>");
    StationWorker.RunResult result = await worker.RunOnceAsync(station, CancellationToken.None);
    Log.Information("Console debug mode finished. country=US processedStations={Processed} failedStations={Failed}",
        result.ProcessedStations, result.FailedStations);

    // Non-zero only when every attempted station failed, so a cron/script wrapper can detect a fully
    // broken pass; a partial success (some processed, some failed) still did useful work.
    return result.ProcessedStations == 0 && result.FailedStations > 0 ? 1 : 0;
}

// Layers the configuration sources: .env at the bottom, then the normal chain, then the plaintext of
// any encrypted environment variables, then the flat-variable aliases that stand in for Spring's
// ${VAR:default} placeholders.
static void ConfigureSources(
    IConfigurationManager configuration,
    List<KeyValuePair<string, string?>> dotenv,
    List<KeyValuePair<string, string?>> decryptedEnv)
{
    configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = dotenv });
    AddDecryptedEnvironment(configuration, decryptedEnv);

    List<KeyValuePair<string, string?>> aliases = EnvironmentAliases.Resolve(configuration);
    if (aliases.Count > 0)
    {
        configuration.Add(new MemoryConfigurationSource { InitialData = aliases });
    }
}

// Overlays the plaintext of any enc:v1: environment variables directly ABOVE the environment-variable
// source, so they replace their own encrypted originals while command-line arguments still win. Adding
// nothing when nothing is encrypted keeps this a no-op for all-plaintext deployments.
static void AddDecryptedEnvironment(IConfigurationBuilder configuration, List<KeyValuePair<string, string?>> decrypted)
{
    if (decrypted.Count == 0)
    {
        return;
    }

    var source = new MemoryConfigurationSource { InitialData = decrypted };

    // The host builders add a DOTNET_/ASPNETCORE_-prefixed environment source for host configuration as
    // well; the unprefixed one is the one carrying DB_URL and friends.
    int envIndex = -1;
    for (int i = 0; i < configuration.Sources.Count; i++)
    {
        if (configuration.Sources[i] is EnvironmentVariablesConfigurationSource { Prefix: null or "" })
        {
            envIndex = i;
        }
    }

    // No unprefixed environment source (never the case for the real host builders) — fall back to
    // highest precedence rather than silently leaving the ciphertext in place.
    configuration.Sources.Insert(envIndex < 0 ? configuration.Sources.Count : envIndex + 1, source);
}

// Structured JSON logging with a 7-day rolling file (logback + logstash-encoder equivalent).
// RenderedCompactJsonFormatter (not the plain compact one) so each line carries the fully rendered
// "@m" message: ServiceLifecycleTracker reads these files back to describe a crash, and a bare message
// template with the values split into properties makes a poor incident summary.
static void ConfigureSerilog(LoggerConfiguration cfg) => cfg
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    // Silence the per-request / per-attempt framework noise. Without these, each station logs several
    // lines — the HttpClient request/response logs, and Polly's execution/retry telemetry, which emits a
    // full stack trace per *handled* retry (a recovered transient timeout). That signal is redundant:
    // ResiliencePipelines logs one concise line on a breaker state change, and the station processors log
    // one line per station that ultimately fails.
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .MinimumLevel.Override("Polly", LogEventLevel.Error)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "debian-weather")
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .WriteTo.File(
        new RenderedCompactJsonFormatter(),
        "logs/weather.log",            // must match Weather:Lifecycle:LogFile
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        fileSizeLimitBytes: 20_000_000,   // ~20 MB per file …
        rollOnFileSizeLimit: true);       // … rolled + capped at 7 files: bounded regardless of volume
