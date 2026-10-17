using AdminService.Data;
using AdminService.Messaging;
using AdminService.Status;

namespace AdminService.Configuration;

/// <summary>
/// Registers every adminservice component. Configuration keys (env file or environment):
/// <list type="table">
///   <item><term><c>DB_URL</c> + <c>DB_USERNAME</c> + <c>DB_PASSWORD</c></term><description>SQL Server, required —
///   without it the service starts but stores nothing (and says so).</description></item>
///   <item><term><c>RABBITMQ_MANAGEMENT_URL</c> + <c>_USERNAME</c> + <c>_PASSWORD</c></term><description>the
///   consumer runs; any of them unset → no consumer (only the own heartbeat is written).</description></item>
///   <item><term><c>STATUS_MACHINE</c></term><description>host name for adminservice's own row.</description></item>
/// </list>
/// </summary>
public static class ServiceRegistration
{
    public sealed record Wiring(bool Sql, bool Consumer);

    public static Wiring AddAdminServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AdminOptions>(configuration.GetSection(AdminOptions.SectionName));
        services.AddSingleton(TimeProvider.System);

        string? dbUrl = NullIfBlank(configuration["DB_URL"]);
        bool useSql = dbUrl is not null;
        if (!useSql)
        {
            return new Wiring(false, false);
        }

        string connectionString = JdbcConnectionString.Build(dbUrl!, configuration["DB_USERNAME"], configuration["DB_PASSWORD"]);
        services.AddSingleton<ISqlConnectionFactory>(new SqlConnectionFactory(connectionString));
        services.AddSingleton<IServiceStatusRepository, SqlServiceStatusRepository>();
        services.AddHostedService<SelfStatusReporter>();

        string? rabbitUrl = NullIfBlank(configuration["RABBITMQ_MANAGEMENT_URL"]);
        string? rabbitUser = NullIfBlank(configuration["RABBITMQ_USERNAME"]);
        string? rabbitPassword = NullIfBlank(configuration["RABBITMQ_PASSWORD"]);
        bool useConsumer = rabbitUrl is not null && rabbitUser is not null && rabbitPassword is not null;
        if (useConsumer)
        {
            services.AddHttpClient<IMessageQueue, RabbitMqManagementClient>(http =>
                RabbitMqManagementClient.Configure(http, rabbitUrl!, rabbitUser!, rabbitPassword!, TimeSpan.FromSeconds(10)));
            services.AddHostedService<ServiceStatusConsumer>();
        }

        return new Wiring(true, useConsumer);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
