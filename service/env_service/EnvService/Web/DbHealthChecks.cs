using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MySqlConnector;

namespace EnvService.Web;

/// <summary>The two connection strings, built once at startup (Configuration/ServiceRegistration).</summary>
public sealed record EnvConnections(string EnvData, string SqlServer);

/// <summary>
/// Readiness: MySQL envdata reachable. Tagged <c>ready</c>, never liveness, so a database blip does not restart
/// the container.
/// </summary>
public sealed class EnvDataHealthCheck(EnvConnections connections) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var connection = new MySqlConnection(connections.EnvData);
            await connection.OpenAsync(ct);
            await using var command = new MySqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MySQL envdata unreachable.", ex);
        }
    }
}

/// <summary>Readiness: SQL Server reachable (same rules as <see cref="EnvDataHealthCheck"/>).</summary>
public sealed class SqlServerHealthCheck(EnvConnections connections) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var connection = new SqlConnection(connections.SqlServer);
            await connection.OpenAsync(ct);
            await using var command = new SqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQL Server unreachable.", ex);
        }
    }
}
