using System.Net;
using Microsoft.Extensions.Options;
using WaterService.Data;
using WaterService.Processing;
using WaterService.Sources;
using WaterService.Web;

namespace WaterService.Configuration;

/// <summary>
/// Registers every water-service component. Shared by both hosting modes (the web host and the
/// <c>--console</c> one-shot host) so they wire up identical dependencies.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddWaterServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkerOptions>(configuration.GetSection(WorkerOptions.SectionName));

        // Datasource: one MySQL envdata connection string (MySqlConnector pools the physical connections).
        string connectionString = BuildConnectionString(configuration);
        services.AddSingleton<ISqlConnectionFactory>(new SqlConnectionFactory(connectionString));

        services.AddWaterResiliencePipelines();

        // Shared, pooled HTTP client for the upstream government feeds.
        services.AddHttpClient("waterSource", (sp, client) =>
            {
                WorkerOptions options = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
                client.Timeout = TimeSpan.FromMilliseconds(options.ReadTimeoutMs);
                if (!client.DefaultRequestHeaders.UserAgent.TryParseAdd(options.UserAgent))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
                }
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                WorkerOptions options = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
                return new SocketsHttpHandler
                {
                    ConnectTimeout = TimeSpan.FromMilliseconds(options.ConnectTimeoutMs),
                    AllowAutoRedirect = true,
                    AutomaticDecompression = DecompressionMethods.All,
                };
            });

        services.AddSingleton<WaterMetrics>();
        services.AddSingleton<WaterStationRepository>();
        services.AddSingleton<WaterDataRepository>();
        services.AddSingleton<IStationHttp503BackoffRepository, StationHttp503BackoffRepository>();
        services.AddSingleton<CsvFetcherCA>();
        services.AddSingleton<UsgsWaterDataClient>();
        services.AddSingleton<StationProcessorCA>();
        services.AddSingleton<StationProcessorUS>();
        services.AddSingleton<StationPostProcessingService>();
        services.AddSingleton<StationHttp503BackoffService>();
        services.AddSingleton<StationWorker>();

        return services;
    }

    /// <summary>
    /// Builds the MySqlConnector connection string to the envdata database from <c>MYSQL_URL</c> /
    /// <c>MYSQL_USERNAME</c> / <c>MYSQL_PASSWORD</c> (<c>jdbc:mysql://</c> URL or native connection string; see
    /// <see cref="MySqlUrl"/>). These are deliberately NOT <c>DB_URL</c> / <c>DB_USERNAME</c> / <c>DB_PASSWORD</c>: the
    /// same env file keeps those for the SQL Server connection, so both connections live side by side and a rollback to
    /// 10.x needs no file change.
    /// </summary>
    public static string BuildConnectionString(IConfiguration configuration)
    {
        string? url = configuration["MYSQL_URL"];
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "MYSQL_URL is not configured (the envdata MySQL connection). Set it as an environment variable or in "
                + "a local .env file; DB_URL is the SQL Server connection and is not used for envdata.");
        }

        return MySqlUrl.Build(url, configuration["MYSQL_USERNAME"], configuration["MYSQL_PASSWORD"]);
    }
}
