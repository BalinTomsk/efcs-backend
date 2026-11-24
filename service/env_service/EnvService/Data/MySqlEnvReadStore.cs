using EnvService.Configuration;
using MySqlConnector;
using Polly;
using Polly.Registry;

namespace EnvService.Data;

/// <summary>
/// <see cref="IEnvReadStore"/> on MySQL envdata views. Every call goes through the <c>sql-read</c> pipeline (one short
/// retry, its own breaker), so an HTTP caller is never held for the jobs' 2 s retries and a burst of failing requests
/// never opens the jobs' breaker.
/// </summary>
public sealed class MySqlEnvReadStore(string connectionString, ResiliencePipelineProvider<string> pipelines) : IEnvReadStore
{
    private readonly ResiliencePipeline _sql = pipelines.GetPipeline(ResiliencePipelines.SqlRead);

    public Task<IReadOnlyList<StationInfo>> StationInfosAsync(CancellationToken ct) =>
        _sql.ExecuteAsync(async token =>
        {
            var rows = new List<StationInfo>(12_500);
            await using MySqlConnection connection = await OpenAsync(token);
            await using var command = new MySqlCommand(
                "SELECT sid, mli, ws_id, country, state, lat, lon, tz, loc_type, loc_name, lake_id, lake_name, elevation, " +
                "agency, has_fish, supported, upd_data, weather_stamp FROM v_station_info", connection)
            { CommandTimeout = 60 };
            await using MySqlDataReader r = await command.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token))
            {
                rows.Add(new StationInfo(
                    Convert.ToInt32(r.GetValue(0)), r.GetString(1), r.GetString(2), r.GetString(3), S(r, 4),
                    r.GetDouble(5), r.GetDouble(6), I(r, 7), Convert.ToInt32(r.GetValue(8)), r.GetString(9),
                    S(r, 10), r.GetString(11), I(r, 12), S(r, 13), Convert.ToInt32(r.GetValue(14)) != 0,
                    Convert.ToInt32(r.GetValue(15)) != 0, Utc(r, 16), Utc(r, 17)));
            }
            return (IReadOnlyList<StationInfo>)rows;
        }, ct).AsTask();

    public Task<IReadOnlyList<StationStateRow>> CurrentAsync(IReadOnlyCollection<int> sids, CancellationToken ct)
    {
        if (sids.Count == 0)
        {
            return Task.FromResult((IReadOnlyList<StationStateRow>)[]);
        }
        return _sql.ExecuteAsync(async token =>
        {
            var rows = new List<StationStateRow>(sids.Count);
            await using MySqlConnection connection = await OpenAsync(token);
            await using var command = new MySqlCommand(
                "SELECT sid, mli, ts, discharge, turbidity, oxygen, ph, elevation, flow_state, q_ratio_1d, q_ratio_2d, " +
                "q_ratio_3_7d, q_median_30d, days_since_peak, flow_stamp FROM v_station_state WHERE sid IN (" +
                Placeholders(sids.Count) + ")", connection);
            AddSids(command, sids);
            await using MySqlDataReader r = await command.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token))
            {
                rows.Add(new StationStateRow(
                    Convert.ToInt32(r.GetValue(0)), r.GetString(1), DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc),
                    D(r, 3), D(r, 4), D(r, 5), M(r, 6), D(r, 7), S(r, 8), D(r, 9), D(r, 10), D(r, 11), D(r, 12),
                    r.IsDBNull(13) ? null : Convert.ToByte(r.GetValue(13)), Utc(r, 14)));
            }
            return (IReadOnlyList<StationStateRow>)rows;
        }, ct).AsTask();
    }

    public Task<IReadOnlyList<WeatherDayRow>> WeatherAsync(
        IReadOnlyCollection<int> sids, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (sids.Count == 0)
        {
            return Task.FromResult((IReadOnlyList<WeatherDayRow>)[]);
        }
        return _sql.ExecuteAsync(async token =>
        {
            var rows = new List<WeatherDayRow>();
            await using MySqlConnection connection = await OpenAsync(token);
            await using var command = new MySqlCommand(
                "SELECT sid, dt, tm, tm_high, tm_low, tm_day, air_temperature, gpf_day, gpf_night, rain_today, pop, humidity, " +
                "wind_max_speed, wind_degree, wind_direction, pressure_mm, weather_code, icon, short_text, long_text, " +
                "provider_type, stamp, pressure_state, d1_mm, front_dt, swing_mm, strength FROM v_weather_day " +
                "WHERE sid IN (" + Placeholders(sids.Count) + ") AND dt BETWEEN @from AND @to ORDER BY sid, dt", connection);
            AddSids(command, sids);
            command.Parameters.Add("@from", MySqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@to", MySqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
            await using MySqlDataReader r = await command.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token))
            {
                rows.Add(new WeatherDayRow(
                    Convert.ToInt32(r.GetValue(0)), DateOnly.FromDateTime(r.GetDateTime(1)),
                    r.IsDBNull(2) ? null : r.GetTimeSpan(2), D(r, 3), D(r, 4), D(r, 5), I(r, 6),
                    Convert.ToDouble(r.GetValue(7)), Convert.ToDouble(r.GetValue(8)), I(r, 9), I(r, 10), D(r, 11), D(r, 12),
                    D(r, 13), S(r, 14), M(r, 15), I(r, 16), S(r, 17), S(r, 18), S(r, 19), Convert.ToInt32(r.GetValue(20)),
                    DateTime.SpecifyKind(r.GetDateTime(21), DateTimeKind.Utc), S(r, 22), M(r, 23),
                    r.IsDBNull(24) ? null : DateOnly.FromDateTime(r.GetDateTime(24)), M(r, 25), M(r, 26)));
            }
            return (IReadOnlyList<WeatherDayRow>)rows;
        }, ct).AsTask();
    }

    public Task<IReadOnlyList<WaterDayRow>> WaterDaysAsync(int sid, DateOnly from, DateOnly to, CancellationToken ct) =>
        _sql.ExecuteAsync(async token =>
        {
            var rows = new List<WaterDayRow>();
            await using MySqlConnection connection = await OpenAsync(token);
            await using var command = new MySqlCommand(
                "SELECT sid, dt, readings, discharge, level, turbidity, oxygen, ph FROM v_water_day " +
                "WHERE sid = @sid AND dt BETWEEN @from AND @to ORDER BY dt", connection);
            command.Parameters.Add("@sid", MySqlDbType.Int32).Value = sid;
            command.Parameters.Add("@from", MySqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@to", MySqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
            await using MySqlDataReader r = await command.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token))
            {
                rows.Add(new WaterDayRow(
                    Convert.ToInt32(r.GetValue(0)), DateOnly.FromDateTime(r.GetDateTime(1)), Convert.ToInt32(r.GetValue(2)),
                    D(r, 3), D(r, 4), D(r, 5), D(r, 6), M(r, 7)));
            }
            return (IReadOnlyList<WaterDayRow>)rows;
        }, ct).AsTask();

    /// <summary><c>@s0,@s1,…</c>, one placeholder per station (bound by <see cref="AddSids"/>).</summary>
    internal static string Placeholders(int count) =>
        string.Join(",", Enumerable.Range(0, count).Select(i => "@s" + i));

    private static void AddSids(MySqlCommand command, IReadOnlyCollection<int> sids)
    {
        int i = 0;
        foreach (int sid in sids)
        {
            command.Parameters.Add("@s" + i++, MySqlDbType.Int32).Value = sid;
        }
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static double? D(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i));

    private static decimal? M(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDecimal(r.GetValue(i));

    private static int? I(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));

    private static string? S(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTime? Utc(MySqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);
}
