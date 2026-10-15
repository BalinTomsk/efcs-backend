using Microsoft.AspNetCore.Mvc;
using NewsFind.Search;

namespace NewsFind.Web.Controllers;

/// <summary>
/// <c>GET /api/v1/newsfind/search?q=…[&amp;limit=10][&amp;country=CA]</c> — semantic search over published
/// news, one hit per article, best first.
///
/// <para>Lives under <c>/api/v1/newsfind/</c>, not <c>/api/v1/news/</c>: cproxy sends <c>/api/v1/news/*</c> to
/// docapi (which keeps its keyword <c>/news/search</c>), so this service needs a prefix of its own to be
/// routable at the gateway.</para>
/// </summary>
[ApiController]
[Route("api/v1/newsfind")]
[Produces("application/json")]
public sealed class NewsSearchController : ControllerBase
{
    private readonly NewsSearchService _service;

    public NewsSearchController(NewsSearchService service)
    {
        _service = service;
    }

    [HttpGet("search")]
    public async Task<ApiResponse<NewsSearchResult>> Search(
        [FromQuery] string? q, [FromQuery] int? limit, [FromQuery] string? country, CancellationToken cancellationToken) =>
        ApiResponse<NewsSearchResult>.Ok(await _service.SearchAsync(q, limit, country, cancellationToken));
}
