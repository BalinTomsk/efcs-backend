using Microsoft.Extensions.Diagnostics.HealthChecks;
using NewsFind.Data;
using NewsFind.Search;

namespace NewsFind.Web;

/// <summary>Readiness: Qdrant answers. Tagged <c>ready</c> only — a blip must not restart the container.</summary>
public sealed class QdrantHealthCheck(QdrantVectorStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await store.PingAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Qdrant unreachable.", ex);
        }
    }
}

/// <summary>
/// Readiness: MySQL answers. Reported <em>degraded</em>, not unhealthy, when it fails: search is served
/// from Qdrant alone, so a database outage only delays indexing.
/// </summary>
public sealed class MySqlHealthCheck(MySqlNewsArticleSource source) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await source.PingAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("MySQL unreachable; indexing is paused.", ex);
        }
    }
}
