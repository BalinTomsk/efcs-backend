using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration.Memory;
using Prometheus;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using WaterService.Configuration;
using WaterService.Processing;
using WaterService.Web;

// Load a local .env file as the LOWEST-precedence configuration source, so real environment variables
// and appsettings always win (production injects DB_URL/DB_USERNAME/DB_PASSWORD as env vars).
List<KeyValuePair<string, string?>> dotenv = DotEnvLoader.Load()
    .Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value))
    .ToList();

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

bool consoleMode = args.Contains("--console");
string? stationFilter = args
    .FirstOrDefault(a => a.StartsWith("--station=", StringComparison.Ordinal))
    ?.Substring("--station=".Length);

try
{
    return consoleMode
        ? await RunConsoleAsync(args, dotenv, stationFilter)
        : await RunWebAsync(args, dotenv);
}
catch (Exception ex)
{
    Log.Fatal(ex, "water-station-pusher terminated unexpectedly.");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

// ---------------------------------------------------------------------------------------------------

// Normal mode: schedule the hourly cycle and expose /health (8080) + management endpoints (8081).
static async Task<int> RunWebAsync(string[] args, List<KeyValuePair<string, string?>> dotenv)
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = dotenv });
    builder.Services.AddSerilog(ConfigureSerilog);

    // Public probe surface on 8080; actuator-equivalent (metrics/liveness/readiness) on private 8081.
    builder.WebHost.UseUrls("http://0.0.0.0:8080", "http://0.0.0.0:8081");

    builder.Services.AddWaterServices(builder.Configuration);
    builder.Services.AddHealthChecks().AddCheck<DbHealthCheck>("db", tags: new[] { "ready" });
    builder.Services.AddHostedService(sp => sp.GetRequiredService<StationWorker>());

    WebApplication app = builder.Build();

    // Lightweight external probe (Docker HEALTHCHECK target): { status, version, uptime }.
    app.MapGet("/health", () => Results.Json(new
    {
        status = "UP",
        version = AppInfo.Version,
        uptime = AppInfo.UptimeSeconds,
    })).RequireHost("*:8080");

    // Management surface — keep 8081 unpublished. Liveness is process-only (never DB-dependent), so a DB
    // blip does not restart the container; readiness reflects datasource connectivity.
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
        .RequireHost("*:8081");
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") })
        .RequireHost("*:8081");
    app.MapMetrics("/metrics").RequireHost("*:8081");

    await app.RunAsync();
    return 0;
}

// Console mode (--console [--station=<MLI>]): run exactly one cycle, then exit. Identical behaviour to a
// scheduled cycle (both countries in parallel + a single post-processing run).
static async Task<int> RunConsoleAsync(string[] args, List<KeyValuePair<string, string?>> dotenv, string? station)
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
    builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = dotenv });
    builder.Services.AddSerilog(ConfigureSerilog);
    builder.Services.AddWaterServices(builder.Configuration);

    using IHost host = builder.Build();

    StationWorker worker = host.Services.GetRequiredService<StationWorker>();
    Log.Information("Running console debug mode. station={Station}", station ?? "<all>");
    int processed = await worker.RunCycleAsync(station, CancellationToken.None);
    Log.Information("Console debug mode finished. processedStations={Processed}", processed);
    return 0;
}

// Structured JSON logging with a 7-day rolling file (logback + logstash-encoder equivalent). Every entry
// carries service/timestamp/level, plus correlationId/station from the logging context when bound.
static void ConfigureSerilog(LoggerConfiguration cfg) => cfg
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "water-station-pusher")
    .WriteTo.Console(new CompactJsonFormatter())
    .WriteTo.File(
        new CompactJsonFormatter(),
        "logs/water-station-pusher.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7);
