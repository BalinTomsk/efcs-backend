namespace EnvService.Data;

/// <summary>
/// One station of the SQL Server master (<c>env.fn_station_list()</c>), in the shape envdata's
/// <c>sp_station_mirror</c> expects (property names are its JSON names; see <see cref="StationMirrorJson"/>).
/// </summary>
public sealed record StationMaster(
    int Sid, string Mli, Guid Id, string Country, string? State, double Lat, double Lon, int? Tz, int LocType,
    string LocName, Guid? LakeId, string LakeName, int? Elevation, string? Agency, bool HasFish, bool Supported,
    DateTime Stamp);

/// <summary>One row of envdata <c>v_station_state</c> = one row of SQL Server <c>env.StationState</c>.</summary>
public sealed record StationStateRow(
    int Sid, string Mli, DateTime Ts, double? Discharge, double? Turbidity, double? Oxygen, decimal? Ph,
    double? Elevation, string? FlowState, double? QRatio1d, double? QRatio2d, double? QRatio37d, double? QMedian30d,
    byte? DaysSincePeak, DateTime? FlowStamp);

/// <summary>One row of envdata <c>v_station_day</c> = one row of SQL Server <c>env.StationDay</c> (pressure in mm Hg).</summary>
public sealed record StationDayRow(
    int Sid, DateOnly Dt, double? TmDay, double? TmHigh, double? TmLow, int? Pop, int? WeatherCode, decimal? PressureMm,
    string? PressureState, DateOnly? FrontDt, decimal? SwingMm, decimal? Strength, decimal? Season,
    decimal? PressureWeight);

/// <summary>A finished ingest cycle not yet handed over (envdata <c>v_job_run_pending</c>).</summary>
public sealed record PendingJob(long JobRunId, string Service, string Job, DateTime Finished);

/// <summary>Result of an envdata fill procedure: rows written, 0 = skipped (fresh enough), -1 = another run holds the lock.</summary>
public readonly record struct FillResult(int Count)
{
    public bool Skipped => Count == 0;
    public bool Locked => Count < 0;
}

/// <summary>The JSON element of <c>sp_station_mirror</c> for one station.</summary>
public static class StationMirrorJson
{
    public static Dictionary<string, object?> Of(StationMaster s) => new()
    {
        ["sid"] = s.Sid,
        ["mli"] = s.Mli,
        ["id"] = s.Id.ToString(),
        ["country"] = s.Country.Trim(),
        ["state"] = string.IsNullOrWhiteSpace(s.State) ? null : s.State.Trim(),
        ["lat"] = s.Lat,
        ["lon"] = s.Lon,
        ["tz"] = s.Tz,
        ["locType"] = s.LocType,
        ["locName"] = Cut(s.LocName, 255),
        ["lakeId"] = s.LakeId?.ToString(),
        ["lakeName"] = Cut(s.LakeName, 64),
        ["elevation"] = s.Elevation,
        ["agency"] = s.Agency is null ? null : Cut(s.Agency, 128),
        ["hasFish"] = s.HasFish ? 1 : 0,
        ["supported"] = s.Supported ? 1 : 0,
        ["stamp"] = s.Stamp.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string Cut(string value, int max) => value.Length > max ? value[..max] : value;
}
