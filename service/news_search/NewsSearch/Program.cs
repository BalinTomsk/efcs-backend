using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using NewsSearch.Configuration;
using NewsSearch.Status;
using NewsSearch.Web;
using Prometheus;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

// Load a local .env file as the LOWEST-precedence configuration source, so real environment variables and
// appsettings always win (production mounts the env file and points DOTENV_PATH at it). enc:v1: values are
// decrypted here (see SecretCodec).
List<KeyValuePair<string, string?>> dotenv = DotEnvLoader.Load()
    .Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value))
    .ToList();

// Docker's --env-file injects values as real environment variables the loader above never sees; decrypt
// any enc:v1: ones separately. A no-op for all-plaintext deployments.
List<KeyValuePair<string, string?>> decryptedEnv = SecretCodec.DecryptEnvironmentVariables().ToList();

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateLogger(); // not a bootstrap (reloadable) logger: that can be frozen only once per process, which breaks a second test host

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = dotenv });
    AddDecryptedEnvironment(builder.Configuration, decryptedEnv);
    builder.Services.AddSerilog(ConfigureSerilog);

    // Public API + /health on 8080; metrics/liveness/readiness on private 8081 (never publish it).
    builder.WebHost.UseUrls("http://0.0.0.0:8080", "http://0.0.0.0:8081");

    IReadOnlyList<string> sources = builder.Services.AddNewsSearchServices(builder.Configuration);
    // Status for the admin page (efcs-backend adminservice).
    bool statusReporting = builder.Services.AddServiceStatusReporting(builder.Configuration, "news_search");
    builder.Services.AddHealthChecks();
    builder.Services.AddControllers();

    WebApplication app = builder.Build();

    Log.Information("news_search {Version} starting. sources={Sources}", AppInfo.Version,
        sources.Count == 0 ? "none" : string.Join(",", sources));
    Log.Information("Service status reporting {State}.", statusReporting ? "on" : "off (RABBITMQ_* not set)");

    app.UseExceptionHandler();

    // Split the two surfaces by the port the connection ARRIVED on, never by the Host header (waterapi 0.1.1:
    // behind Docker's port mapping the Host header carries the host-side port). Management paths are served
    // only on 8081; everything else is refused there. (TestServer reports LocalPort 0 = the public side.)
    app.Use(async (context, next) =>
    {
        bool onManagementPort = context.Connection.LocalPort == ManagementPort;
        if (onManagementPort != IsManagementPath(context.Request.Path.Value ?? string.Empty))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(ApiResponse.Fail("not_found", "No handler for the requested path"));
            return;
        }
        await next();
    });

    app.MapControllers();
    app.MapFallback(() => Results.Json(ApiResponse.Fail("not_found", "No handler for the requested path"),
        statusCode: StatusCodes.Status404NotFound));

    // No dependency checks: each source reports its own failures as a 503 on its own endpoint.
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = _ => false });
    app.MapMetrics("/metrics");

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "news_search terminated unexpectedly.");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

// Overlays the plaintext of any enc:v1: environment variables directly ABOVE the environment-variable source,
// so they replace their own encrypted originals while command-line arguments still win.
static void AddDecryptedEnvironment(IConfigurationBuilder configuration, List<KeyValuePair<string, string?>> decrypted)
{
    if (decrypted.Count == 0)
    {
        return;
    }

    var source = new MemoryConfigurationSource { InitialData = decrypted };
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
    .Enrich.WithProperty("service", "news_search")
    .WriteTo.Console(new CompactJsonFormatter())
    .WriteTo.File(
        new CompactJsonFormatter(),
        "logs/news_search.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        fileSizeLimitBytes: 20_000_000,
        rollOnFileSizeLimit: true);

/// <summary>Entry-point type, public so integration tests can reference it.</summary>
public partial class Program
{
    /// <summary>The private management port (never published).</summary>
    internal const int ManagementPort = 8081;

    private static readonly HashSet<string> ManagementPaths =
        new(StringComparer.OrdinalIgnoreCase) { "/health/live", "/health/ready", "/metrics" };

    /// <summary>Paths served only on <see cref="ManagementPort"/>.</summary>
    internal static bool IsManagementPath(string path) => ManagementPaths.Contains(path);
}
