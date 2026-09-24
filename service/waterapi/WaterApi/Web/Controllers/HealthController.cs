using Microsoft.AspNetCore.Mvc;

namespace WaterApi.Web.Controllers;

/// <summary>
/// Lightweight <c>GET /health</c> probe (<c>{ status, version, uptime }</c>) used as the Docker HEALTHCHECK
/// target — the deeper DB-aware checks live on the private management port (8081).
/// </summary>
[ApiController]
public sealed class HealthController : ControllerBase
{
    [HttpGet("/health")]
    public object Health() => new
    {
        status = "UP",
        version = AppInfo.Version,
        uptime = AppInfo.UptimeSeconds,
    };
}
