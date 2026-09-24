using System.IO.Compression;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using Prometheus;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using WaterApi.Configuration;
using WaterApi.Web;

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

    // Public API + /health on 8080; metrics/liveness/readiness on private 8081 (never publish it).
    builder.WebHost.UseUrls("http://0.0.0.0:8080", "http://0.0.0.0:8081");

    bool useSql = builder.Services.AddWaterApiServices(builder.Configuration);
    IHealthChecksBuilder health = builder.Services.AddHealthChecks();
    if (useSql)
    {
        health.AddCheck<DbHealthCheck>("db", tags: ["ready"]);
    }

    builder.Services.AddControllers();

    // A full US map is ~16k pins; JSON of that size compresses ~10x, so compress every API response.
    builder.Services.AddResponseCompression(o =>
    {
        o.EnableForHttps = true;
        o.Providers.Add<BrotliCompressionProvider>();
        o.Providers.Add<GzipCompressionProvider>();
    });
    builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
    builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

    WebApplication app = builder.Build();

    app.UseExceptionHandler();
    app.UseResponseCompression();

    Log.Information("waterapi {Version} starting. storage={Storage}", AppInfo.Version, useSql ? "sql" : "in-memory");

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
            await context.Response.WriteAsJsonAsync(ApiResponse.Fail("not_found", "No handler for the requested path"));
            return;
        }
        await next();
    });

    app.MapControllers();

    // An unmapped path (commonly a bot probing /wp-admin, /login …) is a quiet 404 in the envelope, not an
    // error log.
    app.MapFallback(() => Results.Json(ApiResponse.Fail("not_found", "No handler for the requested path"),
        statusCode: StatusCodes.Status404NotFound));

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
    Log.Fatal(ex, "waterapi terminated unexpectedly.");
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
    .MinimumLevel.Override("Polly", LogEventLevel.Error)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "waterapi")
    .WriteTo.Console(new CompactJsonFormatter())
    .WriteTo.File(
        new CompactJsonFormatter(),
        "logs/waterapi.log",
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
