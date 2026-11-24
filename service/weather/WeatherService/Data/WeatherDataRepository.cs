using System.Data;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Polly;
using Polly.Registry;
using WeatherService.Configuration;

namespace WeatherService.Data;

/// <summary>
/// Persists weather payloads into the MySQL <c>envdata</c> database through its procedures
/// (<c>envfish-db/mysql/envdata/script02_Proc.sql</c>; no table is touched directly), records each finished cycle in
/// <c>job_run</c> and runs the weather retention. All SQL work goes through the <c>sql</c> resilience pipeline
/// (retry + circuit breaker).
///
/// <para>Until 10.5.0 this wrote <c>dbo.ows_meteo</c> on SQL Server with a raw <c>UPDATE</c> and a trigger shredded it;
/// <c>sp_weather_save</c> now does both in one call, and stores pressure in mm Hg.</para>
/// </summary>
public class WeatherDataRepository
{
    /// <summary><c>job_run.service</c> of this service's cycles.</summary>
    public const string ServiceName = "weather";

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ResiliencePipeline _sql;
    private readonly ILogger<WeatherDataRepository> _log;

    public WeatherDataRepository(
        ISqlConnectionFactory connectionFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<WeatherDataRepository> log)
    {
        _connectionFactory = connectionFactory;
        _sql = pipelineProvider.GetPipeline(ResiliencePipelines.Sql);
        _log = log;
    }

    /// <summary>
    /// Stores one station's payload and the PROVIDER that produced it (<c>sp_weather_save(mli, type, json)</c>): the
    /// raw document is kept in <c>weather_raw</c> (newest per station), and a canonical
    /// <c>fishfind.weather.forecast/v1</c> envelope is shredded into <c>weather_day</c> (one row per date;
    /// <c>pressureHpa</c> converted to mm Hg). A blank payload is a no-op. A station envdata does not know is logged
    /// and the payload dropped, as an unmatched <c>ows_meteo</c> row was before.
    /// </summary>
    /// <param name="sourceType">provider identity; see <c>WeatherSourceType</c></param>
    public virtual async Task SaveStationDataAsync(
        string mli, string jsonData, int sourceType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mli))
        {
            throw new ArgumentException("mli must not be null or blank", nameof(mli));
        }
        if (string.IsNullOrWhiteSpace(jsonData))
        {
            return;
        }

        int days;
        try
        {
            days = await _sql.ExecuteAsync(async token =>
            {
                await using MySqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandType = CommandType.Text;
                command.CommandText = "CALL sp_weather_save(@mli, @type, @json, @out_days); SELECT @out_days";
                command.Parameters.Add("@mli", MySqlDbType.VarChar, 64).Value = mli;
                command.Parameters.Add("@type", MySqlDbType.Byte).Value = (sbyte)sourceType;
                command.Parameters.Add("@json", MySqlDbType.MediumText).Value = jsonData;
                object? result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return result is null or DBNull ? -1 : Convert.ToInt32(result);
            }, ct).ConfigureAwait(false);
        }
        catch (MySqlException ex) when (ex.SqlState == "45000")
        {
            _log.LogWarning("Station unknown to envdata; payload dropped. mli={Mli} bytes={Bytes}", mli, jsonData.Length);
            return;
        }

        if (days < 0)
        {
            _log.LogWarning("Payload stored but not shredded (not a fishfind.weather.forecast/v1 envelope). "
                + "mli={Mli} type={Type} bytes={Bytes}", mli, sourceType, jsonData.Length);
        }
    }

    /// <summary>
    /// Records one finished cycle in envdata's <c>job_run</c> (<c>sp_job_run_record</c>). env_service picks unhanded
    /// cycles up and does the fish hand-over to SQL Server, which this service did itself until 10.5.0.
    /// </summary>
    public virtual async Task RecordCycleAsync(
        string job, DateTime startedUtc, DateTime finishedUtc, string status, int items, string? note,
        CancellationToken ct = default)
    {
        await _sql.ExecuteAsync(async token =>
        {
            await using MySqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.Text;
            command.CommandText =
                "CALL sp_job_run_record(@service, @job, @started, @finished, @status, @items, @note, @out_job_run_id)";
            command.Parameters.Add("@service", MySqlDbType.VarChar, 32).Value = ServiceName;
            command.Parameters.Add("@job", MySqlDbType.VarChar, 32).Value = job;
            command.Parameters.Add("@started", MySqlDbType.DateTime).Value = startedUtc;
            command.Parameters.Add("@finished", MySqlDbType.DateTime).Value = finishedUtc;
            command.Parameters.Add("@status", MySqlDbType.VarChar, 16).Value = status;
            command.Parameters.Add("@items", MySqlDbType.Int32).Value = items;
            command.Parameters.Add("@note", MySqlDbType.VarChar, 255).Value =
                note is null ? DBNull.Value : (note.Length > 255 ? note[..255] : note);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Weather retention: <c>sp_clean_old_weather_data(NULL)</c> deletes days older than 15 days back.</summary>
    public virtual async Task CleanOldWeatherDataAsync(CancellationToken ct = default)
    {
        await _sql.ExecuteAsync(async token =>
        {
            await using MySqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.Text;
            command.CommandText = "CALL sp_clean_old_weather_data(NULL)";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
