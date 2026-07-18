using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using WaterService.Data;

namespace WaterService.Web;

/// <summary>
/// Readiness health check that verifies the datasource is reachable. Tagged <c>ready</c> so it feeds
/// the readiness probe (<c>/health/ready</c>) but NOT liveness — a transient DB blip must not restart
/// the container.
/// </summary>
public sealed class DbHealthCheck : IHealthCheck
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public DbHealthCheck(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqlConnection connection =
                await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Datasource unreachable.", ex);
        }
    }
}
