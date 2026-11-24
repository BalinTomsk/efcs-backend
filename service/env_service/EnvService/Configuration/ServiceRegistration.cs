using EnvService.Data;
using EnvService.Jobs;
using EnvService.Reads;
using EnvService.Web;
using Polly.Registry;

namespace EnvService.Configuration;

/// <summary>DI wiring of env_service.</summary>
public static class ServiceRegistration
{
    /// <summary>
    /// Both connections are required (no in-memory fallback: a service without its databases has nothing to do):
    /// <c>MYSQL_URL</c>/<c>MYSQL_USERNAME</c>/<c>MYSQL_PASSWORD</c> = envdata, <c>DB_URL</c>/<c>DB_USERNAME</c>/
    /// <c>DB_PASSWORD</c> = SQL Server (the same keys as the other services' env files).
    /// </summary>
    public static IServiceCollection AddEnvServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EnvOptions>(configuration.GetSection(EnvOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddEnvResiliencePipelines();

        EnvConnections connections = BuildConnections(configuration);
        services.AddSingleton(connections);
        services.AddSingleton<IEnvStore>(sp =>
            new MySqlEnvStore(connections.EnvData, sp.GetRequiredService<ResiliencePipelineProvider<string>>()));
        services.AddSingleton<IFishStore>(sp =>
            new SqlFishStore(connections.SqlServer, sp.GetRequiredService<ResiliencePipelineProvider<string>>()));

        services.AddSingleton<IEnvReadStore>(sp =>
            new MySqlEnvReadStore(connections.EnvData, sp.GetRequiredService<ResiliencePipelineProvider<string>>()));
        services.AddSingleton<StationCache>();
        services.AddSingleton<StationCacheJob>();
        services.AddHostedService(sp => sp.GetRequiredService<StationCacheJob>());
        services.AddSingleton<EnvReadService>();

        services.AddSingleton<JobStatus>();
        services.AddSingleton<StationMirrorJob>();
        services.AddSingleton<HandOverJob>();
        services.AddHostedService(sp => sp.GetRequiredService<StationMirrorJob>());
        services.AddHostedService(sp => sp.GetRequiredService<HandOverJob>());

        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddProblemDetails();
        return services;
    }

    public static EnvConnections BuildConnections(IConfiguration configuration)
    {
        string mysql = configuration["MYSQL_URL"] is { Length: > 0 } m
            ? MySqlUrl.Build(m, configuration["MYSQL_USERNAME"], configuration["MYSQL_PASSWORD"])
            : throw new InvalidOperationException("MYSQL_URL (envdata) is not configured.");
        string sql = configuration["DB_URL"] is { Length: > 0 } d
            ? SqlServerUrl.Build(d, configuration["DB_USERNAME"], configuration["DB_PASSWORD"])
            : throw new InvalidOperationException("DB_URL (SQL Server) is not configured.");
        return new EnvConnections(mysql, sql);
    }
}
