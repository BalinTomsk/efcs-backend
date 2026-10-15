using System.Data;
using System.Text;
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
    /// <summary>
    /// Readings per CA upsert batch. SQL Server caps one request at 2,100 parameters and each reading takes
    /// three plus the shared <c>@mli</c>, so 500 (1,501 parameters) leaves headroom. A typical file is one
    /// or two batches.
    /// </summary>
    internal const int MaxReadingsPerBatch = 500;

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
    ///
    /// <para>The calls are sent as parameterised <c>EXEC</c> batches of up to <see cref="MaxReadingsPerBatch"/>
    /// readings, so a station costs one or two round trips instead of one per reading.</para>
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

            // One round trip per chunk, not per reading: a CA file holds ~600 five-minute readings and the
            // DB is ~75 ms away, so a per-row EXEC cost ~45 s per station (the Java twin batches too).
            foreach (Reading[] chunk in unique.Chunk(MaxReadingsPerBatch))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandType = CommandType.Text;
                command.CommandText = BuildExecBatch("dbo.sp_UpdateWaterData", "@mli", chunk.Length, "@s", "@e", "@d");
                command.Parameters.Add("@mli", SqlDbType.VarChar, 64).Value = mli;
                for (int i = 0; i < chunk.Length; i++)
                {
                    Reading reading = chunk[i];
                    // The container runs in UTC, so store the UTC wall-clock (matches a UTC-timezone JVM).
                    command.Parameters.Add("@s" + i, SqlDbType.DateTime2).Value = reading.Stamp.UtcDateTime;
                    command.Parameters.Add("@e" + i, SqlDbType.Float).Value = (object?)reading.WaterLevel ?? DBNull.Value;
                    command.Parameters.Add("@d" + i, SqlDbType.Float).Value = (object?)reading.Discharge ?? DBNull.Value;
                }
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves USGS station series using the legacy <c>dbo.sp_push_us_water_data</c> stored procedure.
    /// Empty variable payloads are ignored rather than failing the whole station save. All of a station's
    /// series go in one parameterised <c>EXEC</c> batch.
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

        List<UsSeriesReading> series = seriesList
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.XmlDoc))
            .ToList();
        if (series.Count == 0)
        {
            return;
        }

        await _sql.ExecuteAsync(async token =>
        {
            await using SqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using SqlTransaction transaction =
                (SqlTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);

            // All of a station's series in one round trip (see SaveStationDataAsync).
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandType = CommandType.Text;
            command.CommandText = BuildExecBatch("dbo.sp_push_us_water_data", "@mli, @state", series.Count, "@n", "@u", "@x");
            command.Parameters.Add("@mli", SqlDbType.NVarChar, 128).Value = mli;
            command.Parameters.Add("@state", SqlDbType.NVarChar, 128).Value = state;
            for (int i = 0; i < series.Count; i++)
            {
                command.Parameters.Add("@n" + i, SqlDbType.NVarChar, 128).Value = series[i].Name;
                command.Parameters.Add("@u" + i, SqlDbType.VarChar, 64).Value = (object?)series[i].Unit ?? DBNull.Value;
                command.Parameters.Add("@x" + i, SqlDbType.Xml).Value = series[i].XmlDoc;
            }
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

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
    /// Builds a parameterised batch of <paramref name="count"/> <c>EXEC</c> calls of one procedure, so a whole
    /// station is written in one round trip. Call <c>i</c> passes the shared parameters followed by
    /// <c>{prefix}{i}</c> for each per-call prefix, e.g. <c>EXEC dbo.p @mli, @s0, @e0, @d0;</c>.
    /// </summary>
    internal static string BuildExecBatch(string procedure, string sharedParameters, int count, params string[] prefixes)
    {
        var sql = new StringBuilder(count * (procedure.Length + sharedParameters.Length + 32));
        for (int i = 0; i < count; i++)
        {
            sql.Append("EXEC ").Append(procedure).Append(' ').Append(sharedParameters);
            foreach (string prefix in prefixes)
            {
                sql.Append(", ").Append(prefix).Append(i);
            }
            sql.Append(";\n");
        }
        return sql.ToString();
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
