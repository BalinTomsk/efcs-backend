using System.Runtime.CompilerServices;
using System.Text.Json;
using EnvService.Configuration;
using MySqlConnector;
using Polly;
using Polly.Registry;

namespace EnvService.Data;

/// <summary>
/// <see cref="IEnvStore"/> on MySQL envdata. OUT parameters travel as <c>@out_*</c> session variables
/// (<c>AllowUserVariables</c>, see <see cref="MySqlUrl"/>). Every call goes through the <c>sql</c> pipeline except the
/// streamed day read (a retry mid-stream would duplicate rows; the hand-over simply runs again next time).
/// </summary>
public sealed class MySqlEnvStore(string connectionString, ResiliencePipelineProvider<string> pipelines) : IEnvStore
{
    private const int MirrorBatch = 1000;
    private readonly ResiliencePipeline _sql = pipelines.GetPipeline(ResiliencePipelines.Sql);

    public async Task<int> MirrorStationsAsync(IReadOnlyList<StationMaster> stations, CancellationToken ct)
    {
        int sent = 0;
        foreach (StationMaster[] chunk in stations.Chunk(MirrorBatch))
        {
            string json = JsonSerializer.Serialize(chunk.Select(StationMirrorJson.Of));
            sent += await ScalarIntAsync("CALL sp_station_mirror(@rows, @out_n); SELECT @out_n",
                c => c.Parameters.Add("@rows", MySqlDbType.JSON).Value = json, ct, timeoutSeconds: 120);
        }
        return sent;
    }

    public async Task<FillResult> FillFlowAsync(CancellationToken ct) =>
        new(await ScalarIntAsync("CALL sp_fill_flow_state(NULL, NULL, NULL, @out_n); SELECT @out_n", _ => { }, ct, 900));

    public async Task<FillResult> FillPressureAsync(CancellationToken ct) =>
        new(await ScalarIntAsync("CALL sp_fill_pressure_state(NULL, NULL, NULL, NULL, @out_n); SELECT @out_n", _ => { }, ct, 900));

    public Task<IReadOnlyList<PendingJob>> PendingJobsAsync(CancellationToken ct) =>
        _sql.ExecuteAsync(async token =>
        {
            var jobs = new List<PendingJob>();
            await using MySqlConnection connection = await OpenAsync(token);
            await using var command = new MySqlCommand(
                "SELECT job_run_id, service, job, finished FROM v_job_run_pending ORDER BY job_run_id", connection);
            await using MySqlDataReader r = await command.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token))
            {
                jobs.Add(new PendingJob(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDateTime(3)));
            }
            return (IReadOnlyList<PendingJob>)jobs;
        }, ct).AsTask();

    public Task<int> MarkHandedOverAsync(long upToJobRunId, CancellationToken ct) =>
        ScalarIntAsync("CALL sp_job_run_handed_over(@up_to, @out_n); SELECT @out_n",
            c => c.Parameters.Add("@up_to", MySqlDbType.Int64).Value = upToJobRunId, ct);

    public Task<IReadOnlyList<StationStateRow>> StationStatesAsync(CancellationToken ct) =>
        _sql.ExecuteAsync(async token =>
        {
            var rows = new List<StationStateRow>(12_000);
            await using MySqlConnection connection = await OpenAsync(token);
            await using var command = new MySqlCommand(
                "SELECT sid, mli, ts, discharge, turbidity, oxygen, ph, elevation, flow_state, q_ratio_1d, q_ratio_2d, " +
                "q_ratio_3_7d, q_median_30d, days_since_peak, flow_stamp FROM v_station_state", connection)
            { CommandTimeout = 300 };
            await using MySqlDataReader r = await command.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token))
            {
                rows.Add(new StationStateRow(
                    Convert.ToInt32(r.GetValue(0)), r.GetString(1), r.GetDateTime(2), D(r, 3), D(r, 4), D(r, 5), M(r, 6),
                    D(r, 7), r.IsDBNull(8) ? null : r.GetString(8), D(r, 9), D(r, 10), D(r, 11), D(r, 12),
                    r.IsDBNull(13) ? null : Convert.ToByte(r.GetValue(13)), r.IsDBNull(14) ? null : r.GetDateTime(14)));
            }
            return (IReadOnlyList<StationStateRow>)rows;
        }, ct).AsTask();

    public async IAsyncEnumerable<StationDayRow> StationDaysAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using MySqlConnection connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(
            "SELECT sid, dt, tm_day, tm_high, tm_low, pop, weather_code, pressure_mm, pressure_state, front_dt, swing_mm, " +
            "strength, season, pressure_weight FROM v_station_day", connection)
        { CommandTimeout = 300 };
        await using MySqlDataReader r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            yield return new StationDayRow(
                Convert.ToInt32(r.GetValue(0)), DateOnly.FromDateTime(r.GetDateTime(1)), D(r, 2), D(r, 3), D(r, 4), I(r, 5),
                I(r, 6), M(r, 7), r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : DateOnly.FromDateTime(r.GetDateTime(9)), M(r, 10), M(r, 11), M(r, 12), M(r, 13));
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

    private Task<int> ScalarIntAsync(string sql, Action<MySqlCommand> configure, CancellationToken ct, int timeoutSeconds = 60) =>
        _sql.ExecuteAsync(async token =>
        {
            await using MySqlConnection connection = await OpenAsync(token);
            await using var command = new MySqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
            configure(command);
            object? result = await command.ExecuteScalarAsync(token);
            return result is null or DBNull ? 0 : Convert.ToInt32(result);
        }, ct).AsTask();

    private static double? D(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i));

    private static decimal? M(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDecimal(r.GetValue(i));

    private static int? I(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));
}
