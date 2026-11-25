using EnvService.Reads;
using Microsoft.AspNetCore.Mvc;

namespace EnvService.Web.Controllers;

/// <summary>
/// The public read API (port 8080) for stations and water bodies, plan step 6a. Envelope <c>{data,error,meta}</c>;
/// metric, pressure in mm Hg; times UTC. Contracts: <c>docs/api-reference.html</c>.
/// </summary>
[ApiController]
[Route("api/v1/env")]
public sealed class EnvController(EnvReadService reads) : ControllerBase
{
    /// <summary>
    /// The station map (plan step 6c, replaces waterapi's <c>/api/v1/water/station/map</c>): every pin of a country, or
    /// one province/state, from the station cache. <c>If-None-Match</c> with the current ETag answers 304.
    /// </summary>
    [HttpGet("stations/map")]
    public IActionResult StationMap([FromQuery] string? country, [FromQuery] string? state)
    {
        StationMapResult map = reads.StationMap(country, state);
        Response.Headers.ETag = map.ETag;
        Response.Headers.CacheControl = $"public, max-age={EnvReadService.MapClientMaxAgeSeconds}";
        if (Request.Headers.IfNoneMatch.Any(v => v is not null && v.Split(',').Any(t => t.Trim() == map.ETag)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }
        return Ok(ApiResponse<StationMapDto>.Ok(map.Body));
    }

    /// <summary>One station: identity, place, water-body link, last reading/weather time.</summary>
    [HttpGet("stations/{sid}")]
    public ApiResponse<StationDto> Station(string sid) => ApiResponse<StationDto>.Ok(reads.Station(Sid(sid)));

    /// <summary>The station's newest values and flow state; <c>current</c> null = it never reported.</summary>
    [HttpGet("stations/{sid}/current")]
    public async Task<ApiResponse<StationCurrentDto>> Current(string sid, CancellationToken ct) =>
        ApiResponse<StationCurrentDto>.Ok(await reads.CurrentAsync(Sid(sid), ct));

    /// <summary>Daily weather <c>from</c>..<c>to</c> (yyyy-MM-dd, default 15 back .. 15 ahead, at most 31 days).</summary>
    [HttpGet("stations/{sid}/weather")]
    public async Task<ApiResponse<StationWeatherDto>> Weather(
        string sid, [FromQuery] string? from, [FromQuery] string? to, CancellationToken ct) =>
        ApiResponse<StationWeatherDto>.Ok(await reads.WeatherAsync(Sid(sid), from, to, ct));

    /// <summary>The stations of a water body (empty when it has none).</summary>
    [HttpGet("lakes/{lakeId}/stations")]
    public ApiResponse<LakeStationsDto> LakeStations(string lakeId) =>
        ApiResponse<LakeStationsDto>.Ok(reads.LakeStations(lakeId));

    /// <summary>Each station of a water body with its current values and the weather of <c>date</c> (default today).</summary>
    [HttpGet("lakes/{lakeId}/weather")]
    public async Task<ApiResponse<LakeWeatherDto>> LakeWeather(string lakeId, [FromQuery] string? date, CancellationToken ct) =>
        ApiResponse<LakeWeatherDto>.Ok(await reads.LakeWeatherAsync(lakeId, date, ct));

    /// <summary>The route takes a string so a bad sid is the envelope's 400, not the framework's.</summary>
    private static int Sid(string sid) =>
        int.TryParse(sid, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n > 0
            ? n
            : throw new InvalidRequestException("sid must be a positive integer");
}
