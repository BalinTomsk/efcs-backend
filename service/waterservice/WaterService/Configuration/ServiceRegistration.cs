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

        // Datasource: one connection string (SqlClient pools the physical connections).
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
        services.AddSingleton<CsvFetcherCA>();
        services.AddSingleton<XmlFetcherUS>();
        services.AddSingleton<StationProcessorCA>();
        services.AddSingleton<StationProcessorUS>();
        services.AddSingleton<StationPostProcessingService>();
        services.AddSingleton<StationWorker>();

        return services;
    }

    /// <summary>
    /// Builds the SqlClient connection string from the shared <c>DB_URL</c>/<c>DB_USERNAME</c>/<c>DB_PASSWORD</c>
    /// configuration (JDBC-style URL supported for parity with the other backend services).
    /// </summary>
    public static string BuildConnectionString(IConfiguration configuration)
    {
        string? dbUrl = configuration["DB_URL"];
        if (string.IsNullOrWhiteSpace(dbUrl))
        {
            throw new InvalidOperationException(
                "DB_URL is not configured. Set it as an environment variable or in a local .env file.");
        }

        return JdbcConnectionString.Build(dbUrl, configuration["DB_USERNAME"], configuration["DB_PASSWORD"]);
    }
}
