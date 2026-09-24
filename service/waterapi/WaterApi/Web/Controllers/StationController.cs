using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WaterApi.Configuration;
using WaterApi.Domain;
using WaterApi.Services;

namespace WaterApi.Web.Controllers;

/// <summary>
/// Water-station endpoints under <c>/api/v1/water/station</c>, serving the frontend map from
/// <see cref="StationMapCache"/> — no request ever queries the database directly.
///
/// <list type="bullet">
///   <item><c>GET /api/v1/water/station/map?country=US[&amp;state=MN]</c> — every map pin for a country
///   (optionally one province/state). Replaces <c>Editor/ViewMap.aspx.cs</c>'s direct
///   <c>SELECT … FROM vMapView</c>. Supports <c>If-None-Match</c> → 304.</item>
///   <item><c>GET /api/v1/water/station/{sid}</c> — one map station by numeric sid.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/v1/water/station")]
[Produces("application/json")]
public sealed class StationController : ControllerBase
{
    private readonly StationService _service;
    private readonly StationCacheOptions _options;

    public StationController(StationService service, IOptions<StationCacheOptions> options)
    {
        _service = service;
        _options = options.Value;
    }

    [HttpGet("map")]
    public async Task<IActionResult> GetMap(
        [FromQuery] string? country, [FromQuery] string? state, CancellationToken cancellationToken)
    {
        StationMapView view = await _service.GetMapAsync(country, state, cancellationToken);

        Response.Headers.ETag = view.ETag;
        Response.Headers.CacheControl = $"public, max-age={_options.ClientMaxAgeSeconds}";
        if (Request.Headers.IfNoneMatch.Any(v => v is not null && v.Split(',').Any(t => t.Trim() == view.ETag)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var body = new StationMapBody(
            view.Snapshot.Country,
            view.State,
            view.Stations.Count,
            view.Snapshot.LoadedUtc,
            view.Stations.Select(StationPin.From).ToList());

        return Ok(ApiResponse<StationMapBody>.Ok(body, new Dictionary<string, object?> { ["stale"] = view.Stale }));
    }

    [HttpGet("{sid:int}")]
    public async Task<ApiResponse<MapStation>> GetStation(int sid, CancellationToken cancellationToken) =>
        ApiResponse<MapStation>.Ok(await _service.GetStationAsync(sid, cancellationToken));
}

/// <summary><c>data</c> of <c>GET /api/v1/water/station/map</c>.</summary>
public sealed record StationMapBody(
    string Country,
    string? State,
    int Count,
    DateTime LoadedUtc,
    IReadOnlyList<StationPin> Stations);

/// <summary>One map pin — <see cref="MapStation"/> without the country, which the envelope already carries.</summary>
public sealed record StationPin(int Sid, string Key, double Lat, double Lon, string State, DateTime Stamp)
{
    public static StationPin From(MapStation s) => new(s.Sid, s.Key, s.Lat, s.Lon, s.State, s.Stamp);
}
