using Microsoft.AspNetCore.Mvc;
using NewsIdx.Domain;
using NewsIdx.Indexing;
using NewsIdx.Search;

namespace NewsIdx.Web.Controllers;

/// <summary>
/// Index maintenance — served <strong>only on the private management port (8081)</strong>; Program.cs refuses
/// these paths on the public one. No credential of its own, exactly like <c>/metrics</c>: the port is never
/// published, so reaching it already means being on the host.
///
/// <list type="bullet">
///   <item><c>POST /admin/sync?full=true</c> — index every published article in MySQL (the initial load /
///   a rebuild after a model change). <c>full=false</c> (default) indexes only missing or changed ones.
///   202, or 409 when a sync is already running.</item>
///   <item><c>GET /admin/sync</c> — progress of the current or last sync.</item>
///   <item><c>POST /admin/index/{newsId}</c> — index (or remove) one article now, synchronously.</item>
/// </list>
/// </summary>
[ApiController]
[Route("admin")]
[Produces("application/json")]
public sealed class AdminController : ControllerBase
{
    private readonly NewsSyncService _sync;
    private readonly NewsIndexer _indexer;
    private readonly IHostApplicationLifetime _lifetime;

    public AdminController(NewsSyncService sync, NewsIndexer indexer, IHostApplicationLifetime lifetime)
    {
        _sync = sync;
        _indexer = indexer;
        _lifetime = lifetime;
    }

    [HttpPost("sync")]
    public IActionResult StartSync([FromQuery] bool full = false) =>
        _sync.TryStart(full, _lifetime.ApplicationStopping)
            ? Accepted(ApiResponse<SyncStatus>.Ok(_sync.Status))
            : Conflict(ApiResponse.Fail("sync_running", "A sync is already running"));

    [HttpGet("sync")]
    public ApiResponse<SyncStatus> SyncStatus() => ApiResponse<SyncStatus>.Ok(_sync.Status);

    [HttpPost("index/{newsId}")]
    public async Task<ApiResponse<IndexOutcome>> IndexOne(string newsId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(newsId, out _))
        {
            throw new InvalidRequestException("newsId must be a GUID");
        }
        return ApiResponse<IndexOutcome>.Ok(await _indexer.IndexAsync(NewsIds.Normalize(newsId), cancellationToken));
    }
}
