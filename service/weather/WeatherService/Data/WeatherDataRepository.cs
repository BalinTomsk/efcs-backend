using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;
using WeatherService.Configuration;

namespace WeatherService.Data;

/// <summary>
/// Persists raw weather payloads into the legacy <c>dbo.ows_meteo</c> table and runs the
/// post-processing procedures used by the original service. All SQL work goes through the <c>sql</c>
/// resilience pipeline (retry + circuit breaker).
///
/// <para>The direct <c>UPDATE dbo.ows_meteo</c> below is an intentional, grandfathered exception to
/// the house rule that application code goes through a view/function/procedure rather than a raw
/// table: it mirrors the legacy <c>WeatherDataWorkerOpen</c> .NET service exactly, and the rule only
/// applies to methods added or changed going forward. If a save procedure for this table (e.g.
/// <c>spSaveStationWeather</c>) is ever introduced, switch this method to call it rather than writing
/// a new raw-table statement elsewhere.</para>
/// </summary>
public class WeatherDataRepository
{
    private const int SourceTypeRawJson = 2;

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
    /// Stores one station's raw provider payload verbatim. A blank payload is a no-op; a payload for
    /// an <c>mli</c> with no matching row is logged and dropped, as there is nothing to update.
    /// </summary>
    public virtual async Task SaveStationDataAsync(string mli, string jsonData, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mli))
        {
            throw new ArgumentException("mli must not be null or blank", nameof(mli));
        }
        if (string.IsNullOrWhiteSpace(jsonData))
        {
            return;
        }

        int rows = await _sql.ExecuteAsync(async token =>
        {
            await using SqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.Text;
            command.CommandText = "UPDATE dbo.ows_meteo SET type = @type, ows = @ows, stamp = GETDATE() WHERE mli = @mli";
            command.Parameters.Add("@type", SqlDbType.Int).Value = SourceTypeRawJson;
            command.Parameters.Add("@ows", SqlDbType.NVarChar, -1).Value = jsonData;
            command.Parameters.Add("@mli", SqlDbType.NVarChar, 128).Value = mli;
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (rows == 0)
        {
            _log.LogWarning("No ows_meteo row matched; payload dropped. mli={Mli} bytes={Bytes}", mli, jsonData.Length);
        }
    }

    /// <summary>Pushes lake species associations down to stations (<c>dbo.spPushSpeciesFromLakeToStation</c>).</summary>
    public virtual Task PushSpeciesFromLakeToStationAsync(CancellationToken ct = default) =>
        ExecuteProcedureAsync("dbo.spPushSpeciesFromLakeToStation", ct);

    /// <summary>Recomputes catch probabilities (<c>dbo.spTotalUpdateProbability</c>).</summary>
    public virtual Task TotalUpdateProbabilityAsync(CancellationToken ct = default) =>
        ExecuteProcedureAsync("dbo.spTotalUpdateProbability", ct);

    /// <summary>Deletes stale weather data after each cycle (<c>dbo.sp_clean_old_weather_data</c>).</summary>
    public virtual Task CleanOldWeatherDataAsync(CancellationToken ct = default) =>
        ExecuteProcedureAsync("dbo.sp_clean_old_weather_data", ct);

    private async Task ExecuteProcedureAsync(string procedure, CancellationToken ct)
    {
        await _sql.ExecuteAsync(async token =>
        {
            await using SqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = procedure;
            // These procedures have no timeout budget of their own and can run for minutes over the full
            // station set; the default 30s would abort them mid-run.
            command.CommandTimeout = 0;
            // ExecuteNonQuery runs the whole batch and tolerates procedures that emit incidental result
            // sets or update counts (they are simply discarded), matching the Java result-set draining.
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
