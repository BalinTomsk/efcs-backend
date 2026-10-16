using Microsoft.AspNetCore.Mvc;
using NewsSearch.Sources;

namespace NewsSearch.Web.Controllers;

/// <summary>
/// <list type="bullet">
///   <item><c>GET /api/v1/newssearch/sources</c> — the sources this service answers, so the page knows which
///   parallel calls to make.</item>
///   <item><c>GET /api/v1/newssearch/{source}?q=…[&amp;limit=10][&amp;country=CA]</c> — one source's hits.</item>
/// </list>
/// Under <c>/api/v1/newssearch/</c> because cproxy routes by prefix and docapi owns <c>/api/v1/news/</c>.
/// </summary>
[ApiController]
[Route("api/v1/newssearch")]
[Produces("application/json")]
public sealed class NewsSearchController : ControllerBase
{
    private readonly SearchService _service;

    public NewsSearchController(SearchService service)
    {
        _service = service;
    }

    [HttpGet("sources")]
    public ApiResponse<IReadOnlyList<SourceInfo>> GetSources() =>
        ApiResponse<IReadOnlyList<SourceInfo>>.Ok(_service.Sources
            .Select(s => new SourceInfo(s.Name, $"/api/v1/newssearch/{s.Name}", s.Description))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList());

    [HttpGet("{source}")]
    public async Task<ApiResponse<SourceResult>> Search(
        string source, [FromQuery] string? q, [FromQuery] int? limit, [FromQuery] string? country,
        CancellationToken cancellationToken) =>
        ApiResponse<SourceResult>.Ok(await _service.SearchAsync(source, q, limit, country, cancellationToken));
}

/// <summary>One entry of <c>GET /api/v1/newssearch/sources</c>.</summary>
public sealed record SourceInfo(string Name, string Path, string Description);
