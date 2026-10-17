using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using Prometheus;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using AdminService.Configuration;
using AdminService.Web;

// Load a local .env file as the LOWEST-precedence configuration source, so real environment variables
// and appsettings always win (production injects DB_URL/DB_USERNAME/DB_PASSWORD via DOTENV_PATH).
// enc:v1: values in the file are decrypted here (see SecretCodec).
List<KeyValuePair<string, string?>> dotenv = DotEnvLoader.Load()
    .Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value))
    .ToList();

// Docker's --env-file injects values as real environment variables the loader above never sees;
// decrypt any enc:v1: ones separately. A no-op for all-plaintext deployments.
List<KeyValuePair<string, string?>> decryptedEnv = SecretCodec.DecryptEnvironmentVariables().ToList();

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = dotenv });
    AddDecryptedEnvironment(builder.Configuration, decryptedEnv);
    builder.Services.AddSerilog(ConfigureSerilog);

    // /health on 8080 (Docker HEALTHCHECK only; nothing is published); metrics/liveness/readiness on private 8081 (never publish it).
    builder.WebHost.UseUrls("http://0.0.0.0:8080", "http://0.0.0.0:8081");

    ServiceRegistration.Wiring wiring = builder.Services.AddAdminServices(builder.Configuration);
    IHealthChecksBuilder health = builder.Services.AddHealthChecks();
    if (wiring.Sql)
    {
        health.AddCheck<DbHealthCheck>("db", tags: ["ready"]);
    }

    builder.Services.AddControllers();

    WebApplication app = builder.Build();


    Log.Information("adminservice {Version} starting. storage={Storage} consumer={Consumer}",
        AppInfo.Version, wiring.Sql ? "sql" : "none", wiring.Consumer ? "on" : "off");
    if (!wiring.Sql)
    {
        Log.Warning("DB_URL is not set: no service status will be stored.");
    }

    // Split the two surfaces by the port the connection ARRIVED on, never by the Host header. The Host
    // header carries whatever port the caller dialled: behind Docker's 8090->8080 mapping that is 8090, so
    // RequireHost("*:8080") (0.1.0) answered 404 to every request through the gateway. Management paths are
    // served only on 8081; everything else is refused there. (TestServer reports LocalPort 0, which reads
    // as the public side.)
    app.Use(async (context, next) =>
    {
        bool onManagementPort = context.Connection.LocalPort == ManagementPort;
        bool managementPath = ManagementPaths.Contains(context.Request.Path.Value ?? string.Empty);
        if (onManagementPort != managementPath)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "not_found" });
            return;
        }
        await next();
    });

    app.MapControllers();

    // An unmapped path (commonly a bot probing /wp-admin, /login …) is a quiet 404 in the envelope, not an
    // error log.
    app.MapFallback(() => Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound));

    // Management surface — keep 8081 unpublished. Liveness is process-only (never DB-dependent), so a DB
    // blip does not restart the container; readiness reflects datasource connectivity.
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
    app.MapMetrics("/metrics");

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "adminservice terminated unexpectedly.");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

// Overlays the plaintext of any enc:v1: environment variables directly ABOVE the environment-variable
// source, so they replace their own encrypted originals while command-line arguments still win.
static void AddDecryptedEnvironment(IConfigurationBuilder configuration, List<KeyValuePair<string, string?>> decrypted)
{
    if (decrypted.Count == 0)
    {
        return;
    }

    var source = new MemoryConfigurationSource { InitialData = decrypted };

    // The host builder also adds a DOTNET_/ASPNETCORE_-prefixed environment source; the unprefixed one is
    // the one carrying DB_URL and friends.
    int envIndex = -1;
    for (int i = 0; i < configuration.Sources.Count; i++)
    {
        if (configuration.Sources[i] is EnvironmentVariablesConfigurationSource { Prefix: null or "" })
        {
            envIndex = i;
        }
    }

    configuration.Sources.Insert(envIndex < 0 ? configuration.Sources.Count : envIndex + 1, source);
}

// Structured JSON logging with a 7-day rolling file, same policy as the sibling services.
static void ConfigureSerilog(LoggerConfiguration cfg) => cfg
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .MinimumLevel.Override("Polly", LogEventLevel.Error)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "adminservice")
    .WriteTo.Console(new CompactJsonFormatter())
    .WriteTo.File(
        new CompactJsonFormatter(),
        "logs/adminservice.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        fileSizeLimitBytes: 20_000_000,
        rollOnFileSizeLimit: true);

/// <summary>Entry-point type, public so integration tests can reference it.</summary>
public partial class Program
{
    /// <summary>The private management port (never published).</summary>
    internal const int ManagementPort = 8081;

    /// <summary>Paths served only on <see cref="ManagementPort"/>.</summary>
    internal static readonly HashSet<string> ManagementPaths =
        new(StringComparer.OrdinalIgnoreCase) { "/health/live", "/health/ready", "/metrics" };
}
