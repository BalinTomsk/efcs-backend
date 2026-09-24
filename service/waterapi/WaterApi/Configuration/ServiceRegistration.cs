using WaterApi.Data;
using WaterApi.Services;
using WaterApi.Web;

namespace WaterApi.Configuration;

/// <summary>
/// Registers every waterapi component.
///
/// <para><strong>Storage choice</strong> (the C# counterpart of docapi's default-vs-<c>jdbc</c> profile):
/// when <c>DB_URL</c> is set, stations come from SQL Server via <see cref="SqlStationQueryRepository"/>
/// and the readiness probe checks the datasource; when it is not, <see cref="InMemoryStationQueryRepository"/>
/// answers with no stations so the service still starts and serves with zero DB config.</para>
/// </summary>
public static class ServiceRegistration
{
    /// <returns><c>true</c> when the SQL Server backing is active.</returns>
    public static bool AddWaterApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StationCacheOptions>(configuration.GetSection(StationCacheOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddWaterApiResiliencePipelines();

        bool useSql = !string.IsNullOrWhiteSpace(configuration["DB_URL"]);
        if (useSql)
        {
            string connectionString = JdbcConnectionString.Build(
                configuration["DB_URL"]!, configuration["DB_USERNAME"], configuration["DB_PASSWORD"]);
            services.AddSingleton<ISqlConnectionFactory>(new SqlConnectionFactory(connectionString));
            services.AddSingleton<IStationQueryRepository, SqlStationQueryRepository>();
        }
        else
        {
            services.AddSingleton<IStationQueryRepository, InMemoryStationQueryRepository>();
        }

        services.AddSingleton<StationMapCache>();
        services.AddSingleton<StationService>();
        services.AddHostedService<StationCacheRefresher>();

        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddProblemDetails();

        return useSql;
    }
}
