using System.Data;
using Microsoft.Data.SqlClient;
using Polly;
using Polly.Registry;
using WaterService.Configuration;
using WaterService.Domain;

namespace WaterService.Data;

/// <summary>
/// Persists parsed station readings into the legacy <c>dbo.WaterData</c> table and runs the
/// post-processing stored procedures. All SQL work is wrapped in the <c>sql</c> resilience pipeline
/// (retry + circuit breaker).
/// </summary>
public sealed class WaterDataRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ResiliencePipeline _sql;

    public WaterDataRepository(
        ISqlConnectionFactory connectionFactory,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _connectionFactory = connectionFactory;
        _sql = pipelineProvider.GetPipeline(ResiliencePipelines.Sql);
    }

    /// <summary>
    /// Saves all readings for a single water station in one transaction.
    ///
    /// <para>Readings are de-duplicated by timestamp (last one wins), then upserted through the legacy
    /// <c>dbo.sp_UpdateWaterData</c> — the CSV "Water Level" maps to the legacy <c>elevation</c> column
    /// and "Discharge" to <c>discharge</c>. The stored procedure itself owns the 15-day retention delete.</para>
    /// </summary>
    public async Task SaveStationDataAsync(string mli, IReadOnlyList<Reading> readings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mli))
        {
            throw new ArgumentException("mli must not be null or blank", nameof(mli));
        }
        if (readings is null || readings.Count == 0)
        {
            return;
        }

        List<Reading> unique = DeduplicateByTimestamp(readings);
        if (unique.Count == 0)
        {
            return;
        }

        await _sql.ExecuteAsync(async token =>
        {
            await using SqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using SqlTransaction transaction =
                (SqlTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandType = CommandType.StoredProcedure;
                command.CommandText = "dbo.sp_UpdateWaterData";

                var pMli = command.Parameters.Add("@mli", SqlDbType.VarChar, 64);
                var pStamp = command.Parameters.Add("@stamp", SqlDbType.DateTime2);
                var pElevation = command.Parameters.Add("@elevation", SqlDbType.Float);
                var pDischarge = command.Parameters.Add("@discharge", SqlDbType.Float);
                await command.PrepareAsync(token).ConfigureAwait(false);

                foreach (Reading reading in unique)
                {
                    pMli.Value = mli;
                    // The container runs in UTC, so store the UTC wall-clock (matches a UTC-timezone JVM).
                    pStamp.Value = reading.Stamp.UtcDateTime;
                    pElevation.Value = (object?)reading.WaterLevel ?? DBNull.Value;
                    pDischarge.Value = (object?)reading.Discharge ?? DBNull.Value;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves USGS station series using the legacy <c>dbo.sp_push_us_water_data</c> stored procedure.
    /// Empty variable payloads are ignored rather than failing the whole station save.
    /// </summary>
    public async Task SaveUsStationDataAsync(
        string mli, string state, IReadOnlyList<UsSeriesReading> seriesList, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mli))
        {
            throw new ArgumentException("mli must not be null or blank", nameof(mli));
        }
        if (string.IsNullOrWhiteSpace(state))
        {
            throw new ArgumentException("state must not be null or blank", nameof(state));
        }
        if (seriesList is null || seriesList.Count == 0)
        {
            return;
        }

        await _sql.ExecuteAsync(async token =>
        {
            await using SqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using SqlTransaction transaction =
                (SqlTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);

            foreach (UsSeriesReading series in seriesList)
            {
                if (series is null || string.IsNullOrWhiteSpace(series.Name) || string.IsNullOrWhiteSpace(series.XmlDoc))
                {
                    continue;
                }

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandType = CommandType.StoredProcedure;
                command.CommandText = "dbo.sp_push_us_water_data";
                command.Parameters.Add("@mli", SqlDbType.NVarChar, 128).Value = mli;
                command.Parameters.Add("@state", SqlDbType.NVarChar, 128).Value = state;
                command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = series.Name;
                command.Parameters.Add("@unit", SqlDbType.VarChar, 64).Value = (object?)series.Unit ?? DBNull.Value;
                command.Parameters.Add("@xmldoc", SqlDbType.Xml).Value = series.XmlDoc;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Pushes lake species associations down to stations (<c>dbo.spPushSpeciesFromLakeToStation</c>).</summary>
    public Task PushSpeciesFromLakeToStationAsync(CancellationToken ct = default) =>
        ExecuteProcedureAsync("dbo.spPushSpeciesFromLakeToStation", ct);

    /// <summary>Deletes stale water data after each cycle (<c>dbo.sp_clean_old_water_data</c>).</summary>
    public Task CleanOldWaterDataAsync(CancellationToken ct = default) =>
        ExecuteProcedureAsync("dbo.sp_clean_old_water_data", ct);

    private async Task ExecuteProcedureAsync(string procedure, CancellationToken ct)
    {
        await _sql.ExecuteAsync(async token =>
        {
            await using SqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = procedure;
            // ExecuteNonQuery runs the whole batch and tolerates procedures that emit incidental result
            // sets or update counts (they are simply discarded), matching the Java result-set draining.
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Collapses duplicate timestamps within a single batch so each station/timestamp pair is saved once,
    /// keeping the latest duplicate.
    /// </summary>
    private static List<Reading> DeduplicateByTimestamp(IReadOnlyList<Reading> readings)
    {
        var unique = new Dictionary<DateTime, Reading>();
        foreach (Reading reading in readings)
        {
            if (reading is null)
            {
                continue;
            }
            unique[reading.Stamp.UtcDateTime] = reading;
        }
        return unique.Values.ToList();
    }
}
