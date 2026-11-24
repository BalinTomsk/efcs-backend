using EnvService.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace EnvService.Web.Controllers;

/// <summary>
/// Operations, on the PRIVATE management port 8081 only (Program.cs refuses <c>/admin/*</c> on 8080):
/// <c>GET /admin/jobs</c> (latest outcome of every job) and <c>POST /admin/run/{job}</c> (run one now and wait;
/// <c>mirror</c> or <c>handover</c>, the latter even with nothing pending). Envelope <c>{data,error,meta}</c>.
/// </summary>
[ApiController]
[Route("admin")]
public sealed class AdminController(JobStatus status, StationMirrorJob mirror, HandOverJob handOver) : ControllerBase
{
    [HttpGet("jobs")]
    public ApiResponse<IReadOnlyList<JobRun>> Jobs() => ApiResponse<IReadOnlyList<JobRun>>.Ok(status.Snapshot());

    [HttpPost("run/{job}")]
    public async Task<ApiResponse<JobRun?>> Run(string job, CancellationToken ct)
    {
        JobRun? run = job.ToLowerInvariant() switch
        {
            StationMirrorJob.Name => await mirror.RunOnceAsync(ct),
            HandOverJob.Name => await handOver.RunOnceAsync(force: true, ct),
            _ => throw new NotFoundException($"Unknown job '{job}' (mirror, handover)"),
        };
        return ApiResponse<JobRun?>.Ok(run);
    }
}
