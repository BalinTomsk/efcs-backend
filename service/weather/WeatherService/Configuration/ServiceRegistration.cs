using System.Net;
using Microsoft.Extensions.Options;
using WeatherService.Canonical;
using WeatherService.Data;
using WeatherService.Processing;
using WeatherService.Reporting;
using WeatherService.Sources;
using WeatherService.Web;

namespace WeatherService.Configuration;

/// <summary>
/// Registers every weather-service component. Shared by both hosting modes (the web host and the
/// <c>--console</c> one-shot host) so they wire up identical dependencies.
/// </summary>
public static class ServiceRegistration
{
    /// <summary>Name of the shared pooled <see cref="HttpClient"/> used for every upstream provider.</summary>
    public const string WeatherHttpClient = "weatherSource";

    public static IServiceCollection AddWeatherServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkerOptions>(configuration.GetSection(WorkerOptions.SectionName));
        services.Configure<ReportOptions>(configuration.GetSection(ReportOptions.SectionName));
        services.Configure<LifecycleOptions>(configuration.GetSection(LifecycleOptions.SectionName));
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));

        // Datasource: one connection string (SqlClient pools the physical connections).
        string connectionString = BuildConnectionString(configuration);
        services.AddSingleton<ISqlConnectionFactory>(new SqlConnectionFactory(connectionString));

        services.AddWeatherResiliencePipelines();
        services.AddSingleton<ProviderRateLimiters>();

        // Shared, pooled HTTP client. Per-provider headers are set per request, since the five providers
        // differ only in User-Agent/Accept and otherwise share these timeouts.
        services.AddHttpClient(WeatherHttpClient, (sp, client) =>
            {
                WorkerOptions options = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
                client.Timeout = TimeSpan.FromMilliseconds(options.ReadTimeoutMs);
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

        services.AddSingleton<WeatherStationRepository>();
        services.AddSingleton<WeatherDataRepository>();
        services.AddSingleton<WeatherGovStationRepository>();
        services.AddSingleton<WeatherStationCoverageRepository>();

        services.AddSingleton<OpenMeteoFetcher>();
        services.AddSingleton<WeatherGovFetcher>();
        services.AddSingleton<WeatherGovStationResolver>();
        services.AddSingleton<VisualCrossingFetcher>();
        services.AddSingleton<GoogleWeatherFetcher>();
        services.AddSingleton<WeatherCanadaFetcher>();
        services.AddSingleton<WundergroundFetcher>();

        // Provider knowledge lives in converters now, not in T-SQL inside a database trigger.
        services.AddSingleton<OpenMeteoConverter>();
        services.AddSingleton<VisualCrossingConverter>();

        services.AddSingleton<StationProcessorOpen>();
        services.AddSingleton<StationProcessorWeatherGov>();
        services.AddSingleton<StationProcessorVisualCrossing>();
        services.AddSingleton<StationProcessorGoogleWeather>();
        services.AddSingleton<StationProcessorWeatherCanada>();
        services.AddSingleton<StationProcessorWunderground>();

        services.AddSingleton<StationPostProcessingService>();
        services.AddSingleton<WeatherApiUsageTracker>();
        services.AddSingleton<CycleReportRecorder>();
        services.AddSingleton<ServiceLifecycleTracker>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<StationWorker>();
        services.AddSingleton<WeeklyReportMailService>();

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
