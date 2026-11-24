using System.Data;
using MySqlConnector;
using Polly;
using Polly.Registry;
using WaterService.Configuration;
using WaterService.Domain;

namespace WaterService.Data;

/// <summary>
/// SQL procedure gateway for per-water-station HTTP 503 backoff state.
/// </summary>
public sealed class StationHttp503BackoffRepository : IStationHttp503BackoffRepository
{
    private const string RefreshDue = "CALL sp_station_503_refresh_due(@today)";
    private const string RecordHttp503 =
        "CALL sp_station_503_record(@provider, @country, @stationMli, @state, @today)";
    private const string Reset = "CALL sp_station_503_reset(@provider, @country, @stationMli)";
    private const string SummaryByState = "CALL sp_station_503_summary_by_state()";

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ResiliencePipeline _sql;

    public StationHttp503BackoffRepository(
        ISqlConnectionFactory connectionFactory,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _connectionFactory = connectionFactory;
        _sql = pipelineProvider.GetPipeline(ResiliencePipelines.Sql);
    }

    public Task RefreshDueAsync(DateOnly today, CancellationToken ct = default) =>
        ExecuteAsync(RefreshDue, ct, command =>
        {
            command.Parameters.Add("@today", MySqlDbType.Date).Value = today.ToDateTime(TimeOnly.MinValue);
        });

    public Task RecordHttp503Async(
        string provider,
        string country,
        string stationMli,
        string state,
        DateOnly today,
        CancellationToken ct = default) =>
        ExecuteAsync(RecordHttp503, ct, command =>
        {
            command.Parameters.Add("@provider", MySqlDbType.VarChar, 64).Value = provider;
            command.Parameters.Add("@country", MySqlDbType.VarChar, 2).Value = country;
            command.Parameters.Add("@stationMli", MySqlDbType.VarChar, 64).Value = stationMli;
            command.Parameters.Add("@state", MySqlDbType.VarChar, 2).Value = state.Length > 2 ? state[..2] : state;
            command.Parameters.Add("@today", MySqlDbType.Date).Value = today.ToDateTime(TimeOnly.MinValue);
        });

    public Task ResetAsync(string provider, string country, string stationMli, CancellationToken ct = default) =>
        ExecuteAsync(Reset, ct, command =>
        {
            command.Parameters.Add("@provider", MySqlDbType.VarChar, 64).Value = provider;
            command.Parameters.Add("@country", MySqlDbType.VarChar, 2).Value = country;
            command.Parameters.Add("@stationMli", MySqlDbType.VarChar, 64).Value = stationMli;
        });

    public async Task<IReadOnlyList<BackoffSummary>> SummaryByStateAsync(CancellationToken ct = default)
    {
        return await _sql.ExecuteAsync(async token =>
        {
            var summaries = new List<BackoffSummary>();
            await using MySqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.Text;
            command.CommandText = SummaryByState;

            await using MySqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            int stateOrdinal = reader.GetOrdinal("state");
            int stageOrdinal = reader.GetOrdinal("backoff_stage");
            int countOrdinal = reader.GetOrdinal("station_count");

            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                string state = reader.IsDBNull(stateOrdinal) ? string.Empty : reader.GetString(stateOrdinal);
                string stage = reader.IsDBNull(stageOrdinal) ? string.Empty : reader.GetString(stageOrdinal);
                long count = reader.IsDBNull(countOrdinal) ? 0L : Convert.ToInt64(reader.GetValue(countOrdinal));
                summaries.Add(new BackoffSummary(state, stage, count));
            }

            return (IReadOnlyList<BackoffSummary>)summaries;
        }, ct).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(
        string procedure,
        CancellationToken ct,
        Action<MySqlCommand> configure)
    {
        await _sql.ExecuteAsync(async token =>
        {
            await using MySqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.Text;
            command.CommandText = procedure;
            configure(command);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
