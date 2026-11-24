using System.Text.Json;
using CsNews.News;
using CsNews.Status;
using Microsoft.AspNetCore.Mvc;

namespace CsNews.Web.Controllers;

/// <summary>
/// The news data API: every read and write of the MySQL <c>news</c> table, for docapi only. docapi keeps the
/// public <c>/api/v1/news/*</c> surface, its validation, role rules and caches, and calls these routes on a miss.
/// <para><strong>Internal.</strong> Reached over the VPC (host port 8091 is bound to localhost + the VPC
/// address). cproxy routes only <c>/api/v1/newssearch/</c> here, so <c>/api/v1/csnews/</c> is never public —
/// never add it to cproxy.</para>
/// <para>Inputs are re-checked cheaply (ids, windows, photo slot), because a bad value must be a 400 here
/// rather than a database fault that counts against the breaker.</para>
/// </summary>
[ApiController]
[Route("api/v1/csnews")]
[Produces("application/json")]
public sealed class NewsDataController : ControllerBase
{
    internal const int MaxIdLength = 64;
    internal const int MaxListLimit = 100;
    internal const int MaxRefLimit = 100;
    internal const int MaxTaggedLimit = 5000;
    internal const int MaxQueryLength = 256;
    internal const int MaxFishIds = 20;

    private readonly INewsStore _store;
    private readonly ServiceActivity? _activity;

    public NewsDataController(INewsStore store, ServiceActivity? activity = null)
    {
        _store = store;
        _activity = activity;
    }

    /// <summary><c>GET /doc/{id}</c>: the article document (<c>sp_news_doc_get</c>).</summary>
    [HttpGet("doc/{id}")]
    public async Task<ApiResponse<JsonElement>> GetDocument(string id, CancellationToken ct)
    {
        Record("news doc");
        return ApiResponse<JsonElement>.Ok(await _store.GetDocumentAsync(Id(id), ct)
            ?? throw new NewsNotFoundException($"No news article {id}"));
    }

    /// <summary><c>GET /export/{id}</c>: the interchange document, drafts included.</summary>
    [HttpGet("export/{id}")]
    public async Task<ApiResponse<JsonElement>> Export(string id, CancellationToken ct)
    {
        Record("news export");
        return ApiResponse<JsonElement>.Ok(await _store.ExportAsync(Id(id), ct)
            ?? throw new NewsNotFoundException($"No news article {id}"));
    }

    /// <summary><c>GET /list?country=&amp;offset=&amp;limit=&amp;order=date|edited</c>.</summary>
    [HttpGet("list")]
    public async Task<ApiResponse<NewsListPage>> List([FromQuery] string? country, [FromQuery] int offset = 0,
        [FromQuery] int limit = 20, [FromQuery] string? order = null, CancellationToken ct = default)
    {
        Record("news list");
        NewsListOrder sort = order?.Trim().ToLowerInvariant() switch
        {
            null or "" or "date" => NewsListOrder.Date,
            "edited" => NewsListOrder.Edited,
            _ => throw new NewsRequestException("order must be 'date' or 'edited'"),
        };
        return ApiResponse<NewsListPage>.Ok(await _store.ListAsync(Country(country), Offset(offset),
            Limit(limit, MaxListLimit), sort, ct));
    }

    /// <summary><c>GET /default</c>: the home-page document set.</summary>
    [HttpGet("default")]
    public async Task<ApiResponse<NewsDefaultPage>> Default(CancellationToken ct)
    {
        Record("news default");
        return ApiResponse<NewsDefaultPage>.Ok(await _store.DefaultAsync(ct));
    }

    /// <summary><c>GET /photo/{id}</c>: the raw lead-photo bytes of a published article.</summary>
    [HttpGet("photo/{id}")]
    [Produces("application/octet-stream", "application/json")]
    public async Task<IActionResult> Photo(string id, CancellationToken ct)
    {
        Record("news photo");
        byte[]? bytes = await _store.PhotoAsync(Id(id), ct);
        return bytes is { Length: > 0 }
            ? File(bytes, "application/octet-stream")
            : throw new NewsNotFoundException($"No photo for news article {id}");
    }

    /// <summary><c>GET /search?q=&amp;fishId=…(repeat)&amp;country=&amp;offset=&amp;limit=</c>: SQL LIKE search.</summary>
    [HttpGet("search")]
    public async Task<ApiResponse<NewsSearchPage>> Search([FromQuery] string? q, [FromQuery] string[]? fishId,
        [FromQuery] string? country, [FromQuery] int offset = 0, [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        Record("news search sql");
        string term = q?.Trim() ?? string.Empty;
        if (term.Length == 0 || term.Length > MaxQueryLength)
        {
            throw new NewsRequestException($"q is required and at most {MaxQueryLength} characters");
        }
        List<string> fish = (fishId ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Id(f.Trim())).Distinct().ToList();
        if (fish.Count > MaxFishIds)
        {
            throw new NewsRequestException($"at most {MaxFishIds} fishId values");
        }
        return ApiResponse<NewsSearchPage>.Ok(await _store.SearchAsync(
            new NewsSearchQuery(term, fish, Country(country), Offset(offset), Limit(limit, MaxListLimit)), ct));
    }

    /// <summary><c>GET /lake/{id}?limit=</c>: one water body's news panel.</summary>
    [HttpGet("lake/{id}")]
    public async Task<ApiResponse<NewsLakePage>> Lake(string id, [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        Record("news lake");
        return ApiResponse<NewsLakePage>.Ok(await _store.LakeAsync(Id(id), Limit(limit, MaxRefLimit), ct));
    }

    /// <summary><c>GET /fish/{id}?limit=</c>: one species' news panel.</summary>
    [HttpGet("fish/{id}")]
    public async Task<ApiResponse<NewsFishPage>> Fish(string id, [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        Record("news fish");
        return ApiResponse<NewsFishPage>.Ok(await _store.FishAsync(Id(id), Limit(limit, MaxRefLimit), ct));
    }

    /// <summary><c>GET /tagged?limit=</c>: newest published stories that carry a water body (docapi /news/near).</summary>
    [HttpGet("tagged")]
    public async Task<ApiResponse<IReadOnlyList<TaggedNews>>> Tagged([FromQuery] int limit = 2000, CancellationToken ct = default)
    {
        Record("news tagged");
        return ApiResponse<IReadOnlyList<TaggedNews>>.Ok(await _store.TaggedAsync(Limit(limit, MaxTaggedLimit), ct));
    }

    /// <summary><c>POST /doc</c>: create one published article (<c>sp_news_doc_insert</c>) → 201 <c>{newsId}</c>.</summary>
    [HttpPost("doc")]
    public async Task<IActionResult> Insert([FromBody] NewsWrite? write, CancellationToken ct)
    {
        Record("news insert");
        string id = await _store.InsertAsync(Write(write), ct);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<NewsIdResult>.Ok(new NewsIdResult(id)));
    }

    /// <summary><c>PUT /doc/{id}</c>: replace one article (<c>sp_news_doc_update</c>); 404 when unknown.</summary>
    [HttpPut("doc/{id}")]
    public async Task<ApiResponse<NewsUpdateResult>> Update(string id, [FromBody] NewsWrite? write, CancellationToken ct)
    {
        Record("news update");
        return await _store.UpdateAsync(Id(id), Write(write), ct)
            ? ApiResponse<NewsUpdateResult>.Ok(new NewsUpdateResult(id, true))
            : throw new NewsNotFoundException($"No news article {id}");
    }

    /// <summary><c>POST /admin/draft</c>: purge drafts, create one → <c>{newsId}</c>.</summary>
    [HttpPost("admin/draft")]
    public async Task<ApiResponse<NewsIdResult>> CreateDraft(CancellationToken ct)
    {
        Record("news draft");
        return ApiResponse<NewsIdResult>.Ok(new NewsIdResult(await _store.CreateDraftAsync(ct)));
    }

    /// <summary><c>POST /admin/publish</c>: upsert + publish → <c>{newsId, action}</c>.</summary>
    [HttpPost("admin/publish")]
    public async Task<ApiResponse<NewsPublishResult>> Publish([FromBody] NewsPublishRequest? request, CancellationToken ct)
    {
        Record("news publish");
        if (request is null)
        {
            throw new NewsRequestException("request body is required");
        }
        Id(request.NewsId);
        return ApiResponse<NewsPublishResult>.Ok(await _store.PublishAsync(request, ct));
    }

    /// <summary><c>PUT /admin/{id}/photo/{index}</c>: replace one photo slot → <c>{found, updated}</c>.</summary>
    [HttpPut("admin/{id}/photo/{index:int}")]
    public async Task<ApiResponse<NewsPhotoUpdateResult>> UpdatePhoto(string id, int index,
        [FromBody] NewsPhotoUpdateRequest? request, CancellationToken ct)
    {
        Record("news photo update");
        if (index is < 0 or > 2)
        {
            throw new NewsRequestException("index must be 0, 1 or 2");
        }
        if (string.IsNullOrEmpty(request?.Data))
        {
            throw new NewsRequestException("data (base64 photo) is required");
        }
        return ApiResponse<NewsPhotoUpdateResult>.Ok(await _store.UpdatePhotoAsync(Id(id), index,
            MySqlNewsStore.Base64(request.Data), request.Author, request.Alt, ct));
    }

    /// <summary><c>GET /ping</c>: 200 when MySQL answers <c>SELECT 1</c>, else 503 (docapi's cache evictor asks).</summary>
    [HttpGet("ping")]
    public async Task<ApiResponse<object>> Ping(CancellationToken ct)
    {
        _activity?.Record("news ping", request: false);
        await _store.PingAsync(ct);
        return ApiResponse<object>.Ok(new { mysql = "up" });
    }

    // ---- input checks ---------------------------------------------------------------------------------------

    private void Record(string action) => _activity?.Record(action);

    internal static string Id(string? id)
    {
        string value = id?.Trim() ?? string.Empty;
        return value.Length is 0 or > MaxIdLength
            ? throw new NewsRequestException($"id is required and at most {MaxIdLength} characters")
            : value;
    }

    private static string? Country(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return null;
        }
        string value = country.Trim().ToUpperInvariant();
        return value.Length == 2 && value.All(char.IsAsciiLetter)
            ? value
            : throw new NewsRequestException("country must be two letters");
    }

    private static int Offset(int offset) =>
        offset < 0 ? throw new NewsRequestException("offset must not be negative") : offset;

    private static int Limit(int limit, int max) =>
        limit < 1 || limit > max ? throw new NewsRequestException($"limit must be 1..{max}") : limit;

    private static NewsWrite Write(NewsWrite? write) =>
        write is null || string.IsNullOrWhiteSpace(write.Title)
            ? throw new NewsRequestException("a body with a non-blank title is required")
            : write;
}
