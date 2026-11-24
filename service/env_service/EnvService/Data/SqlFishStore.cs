using System.Data;
using EnvService.Configuration;
using Microsoft.Data.SqlClient;
using Polly;
using Polly.Registry;

namespace EnvService.Data;

/// <summary>
/// <see cref="IFishStore"/> on SQL Server, schema <c>env</c> only. Pushes go as ONE table-valued parameter per call
/// (<c>env.StationStateRows</c> / <c>env.StationDayRows</c>): the column order of each DataTable is the type's.
/// </summary>
public sealed class SqlFishStore(string connectionString, ResiliencePipelineProvider<string> pipelines) : IFishStore
{
    private readonly ResiliencePipeline _sql = pipelines.GetPipeline(ResiliencePipelines.Sql);

    public Task<IReadOnlyList<StationMaster>> StationListAsync(CancellationToken ct) =>
        _sql.ExecuteAsync(async token =>
        {
            var rows = new List<StationMaster>(12_000);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using var command = new SqlCommand(
                "SELECT sid, mli, id, country, state, lat, lon, tz, locType, locName, lakeId, lakeName, elevation, agency, " +
                "hasFish, supported, stamp FROM env.fn_station_list()", connection)
            { CommandTimeout = 120 };
            await using SqlDataReader r = await command.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token))
            {
                rows.Add(new StationMaster(
                    r.GetInt32(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                    r.GetDouble(5), r.GetDouble(6), r.IsDBNull(7) ? null : r.GetInt32(7), r.GetInt32(8), r.GetString(9),
                    r.IsDBNull(10) ? null : r.GetGuid(10), r.GetString(11), r.IsDBNull(12) ? null : r.GetInt32(12),
                    r.IsDBNull(13) ? null : r.GetString(13), r.GetBoolean(14), r.GetBoolean(15), r.GetDateTime(16)));
            }
            return (IReadOnlyList<StationMaster>)rows;
        }, ct).AsTask();

    public Task<int> PushStationStatesAsync(IReadOnlyList<StationStateRow> rows, CancellationToken ct) =>
        PushAsync("env.sp_push_station_state", "env.StationStateRows", StateTable(rows), ct);

    public Task<int> PushStationDaysAsync(IReadOnlyList<StationDayRow> rows, CancellationToken ct) =>
        PushAsync("env.sp_push_station_day", "env.StationDayRows", DayTable(rows), ct);

    public Task<int> TrimStationDaysAsync(DateOnly from, CancellationToken ct) =>
        _sql.ExecuteAsync(async token =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using var command = new SqlCommand("env.sp_station_day_trim", connection)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = 120 };
            command.Parameters.Add("@from", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
            return Convert.ToInt32(await command.ExecuteScalarAsync(token) ?? 0);
        }, ct).AsTask();

    public Task<int> PushSpeciesAsync(CancellationToken ct) => ScalarProcAsync("env.sp_push_species", 1800, ct);

    public Task<int> UpdateProbabilityAsync(CancellationToken ct) => ScalarProcAsync("env.sp_update_probability", 1800, ct);

    /// <summary>A parameterless env procedure whose first result column is its outcome. Long timeout: the species push
    /// and the probability update scan all ~237,000 fish_location rows.</summary>
    private Task<int> ScalarProcAsync(string procedure, int timeoutSeconds, CancellationToken ct) =>
        _sql.ExecuteAsync(async token =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using var command = new SqlCommand(procedure, connection)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = timeoutSeconds };
            return Convert.ToInt32(await command.ExecuteScalarAsync(token) ?? 0);
        }, ct).AsTask();

    private Task<int> PushAsync(string procedure, string typeName, DataTable table, CancellationToken ct) =>
        _sql.ExecuteAsync(async token =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using var command = new SqlCommand(procedure, connection)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
            SqlParameter p = command.Parameters.AddWithValue("@rows", table);
            p.SqlDbType = SqlDbType.Structured;
            p.TypeName = typeName;
            return Convert.ToInt32(await command.ExecuteScalarAsync(token) ?? 0);
        }, ct).AsTask();

    /// <summary>A DataTable in <c>env.StationStateRows</c> column order.</summary>
    internal static DataTable StateTable(IEnumerable<StationStateRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("sid", typeof(int));
        t.Columns.Add("mli", typeof(string));
        t.Columns.Add("ts", typeof(DateTime));
        t.Columns.Add("discharge", typeof(double));
        t.Columns.Add("turbidity", typeof(double));
        t.Columns.Add("oxygen", typeof(double));
        t.Columns.Add("ph", typeof(decimal));
        t.Columns.Add("elevation", typeof(double));
        t.Columns.Add("flow_state", typeof(string));
        t.Columns.Add("q_ratio_1d", typeof(double));
        t.Columns.Add("q_ratio_2d", typeof(double));
        t.Columns.Add("q_ratio_3_7d", typeof(double));
        t.Columns.Add("q_median_30d", typeof(double));
        t.Columns.Add("days_since_peak", typeof(byte));
        t.Columns.Add("flow_stamp", typeof(DateTime));
        foreach (StationStateRow r in rows)
        {
            t.Rows.Add(r.Sid, r.Mli, r.Ts, N(r.Discharge), N(r.Turbidity), N(r.Oxygen), N(r.Ph), N(r.Elevation),
                N(r.FlowState), N(r.QRatio1d), N(r.QRatio2d), N(r.QRatio37d), N(r.QMedian30d), N(r.DaysSincePeak),
                N(r.FlowStamp));
        }
        return t;
    }

    /// <summary>A DataTable in <c>env.StationDayRows</c> column order.</summary>
    internal static DataTable DayTable(IEnumerable<StationDayRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("sid", typeof(int));
        t.Columns.Add("dt", typeof(DateTime));
        t.Columns.Add("tm_day", typeof(double));
        t.Columns.Add("tm_high", typeof(double));
        t.Columns.Add("tm_low", typeof(double));
        t.Columns.Add("pop", typeof(int));
        t.Columns.Add("weather_code", typeof(int));
        t.Columns.Add("pressure_mm", typeof(decimal));
        t.Columns.Add("pressure_state", typeof(string));
        t.Columns.Add("front_dt", typeof(DateTime));
        t.Columns.Add("swing_mm", typeof(decimal));
        t.Columns.Add("strength", typeof(decimal));
        t.Columns.Add("season", typeof(decimal));
        t.Columns.Add("pressure_weight", typeof(decimal));
        foreach (StationDayRow r in rows)
        {
            t.Rows.Add(r.Sid, r.Dt.ToDateTime(TimeOnly.MinValue), N(r.TmDay), N(r.TmHigh), N(r.TmLow), N(r.Pop),
                N(r.WeatherCode), N(r.PressureMm), N(r.PressureState),
                r.FrontDt is DateOnly f ? f.ToDateTime(TimeOnly.MinValue) : DBNull.Value, N(r.SwingMm), N(r.Strength),
                N(r.Season), N(r.PressureWeight));
        }
        return t;
    }

    private static object N<T>(T? value) where T : struct => value.HasValue ? value.Value : DBNull.Value;

    private static object N(string? value) => value is null ? DBNull.Value : value;
}
