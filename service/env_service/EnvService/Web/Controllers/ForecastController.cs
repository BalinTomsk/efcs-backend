using System.Text.Json.Nodes;
using EnvService.Reads;
using Microsoft.AspNetCore.Mvc;

namespace EnvService.Web.Controllers;

/// <summary>
/// The forecast documents (port 8080, plan step 6b): the bodies docapi's <c>/api/v1/forecast/*</c> returns from step
/// 6c on. Same parameters as docapi (<c>fish</c>, <c>lat</c>, <c>lon</c>, <c>radius</c>, <c>country</c>, <c>limit</c>,
/// <c>day</c>); envelope <c>{data,error,meta}</c>; pressure in mm Hg; <c>Cache-Control: private, max-age=300</c>.
/// </summary>
[ApiController]
[Route("api/v1/env/forecast")]
public sealed class ForecastController(ForecastService forecast) : ControllerBase
{
    [HttpGet("stations")]
    public async Task<ApiResponse<JsonNode>> Stations(
        [FromQuery] string? fish, [FromQuery] string? lat, [FromQuery] string? lon, [FromQuery] string? radius,
        [FromQuery] string? country, [FromQuery] string? limit, CancellationToken ct) =>
        Ok(await forecast.StationsAsync(fish, lat, lon, radius, country, limit, ct));

    [HttpGet("plot/{sid}")]
    public async Task<ApiResponse<JsonNode>> Plot(string sid, [FromQuery] string? fish, CancellationToken ct) =>
        Ok(await forecast.PlotAsync(sid, fish, ct));

    [HttpGet("reasoning/{sid}")]
    public async Task<ApiResponse<JsonNode>> Reasoning(string sid, [FromQuery] string? fish, [FromQuery] string? day,
        CancellationToken ct) =>
        Ok(await forecast.ReasoningAsync(sid, fish, day, ct));

    private ApiResponse<JsonNode> Ok(JsonNode doc)
    {
        Response.Headers.CacheControl = "private, max-age=300";
        return ApiResponse<JsonNode>.Ok(doc);
    }
}
