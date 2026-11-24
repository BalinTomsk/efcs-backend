using System.Data;
using System.Globalization;
using System.Text.Json;
using MySqlConnector;
using Polly;
using Polly.Registry;
using WaterService.Configuration;
using WaterService.Domain;

namespace WaterService.Data;

/// <summary>
/// Persists parsed station readings into the MySQL <c>envdata</c> database through its procedures
/// (<c>envfish-db/mysql/envdata/script02_Proc.sql</c>; no table is touched directly), records each finished cycle in
/// <c>job_run</c> and runs the nightly retention. All SQL work is wrapped in the <c>sql</c> resilience pipeline
/// (retry + circuit breaker).
/// </summary>
public sealed class WaterDataRepository
{
    /// <summary>
    /// Readings per CA call. One call carries a JSON array, so there is no parameter cap any more; 500 keeps a call
    /// short (a typical file is one or two calls) and bounds the statement envdata expands with JSON_TABLE.
    /// </summary>
    internal const int MaxReadingsPerBatch = 500;

    /// <summary><c>job_run.service</c> of this service's cycles.</summary>
    public const string ServiceName = "waterservice";

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
    /// Saves all readings for a single Canadian station in one transaction.
    ///
    /// <para>Readings are de-duplicated by timestamp (last one wins), then upserted through
    /// <c>sp_water_save_ca(mli, rows)</c>: the CSV "Water Level" is <c>e</c>, "Discharge" is <c>d</c>, the time is UTC.
    /// The procedure overwrites a reading at the same time, stores <c>-999</c> as NULL and refreshes the station's
    /// current state. Retention is not per call any more: <see cref="CleanOldWaterDataAsync"/> drops whole days.</para>
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
            await using MySqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using MySqlTransaction transaction =
                await connection.BeginTransactionAsync(token).ConfigureAwait(false);

            foreach (Reading[] chunk in unique.Chunk(MaxReadingsPerBatch))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandType = CommandType.Text;
                command.CommandText = "CALL sp_water_save_ca(@mli, @rows)";
                command.Parameters.Add("@mli", MySqlDbType.VarChar, 64).Value = mli;
                command.Parameters.Add("@rows", MySqlDbType.JSON).Value = BuildCaRowsJson(chunk);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves all of one USGS station's series in ONE call to <c>sp_water_save_us(mli, state, series)</c>, which merges
    /// every series of a day into one reading, converts the units and refreshes the station's current state
    /// (until 10.8.0 each series was its own <c>sp_push_us_water_data</c> EXEC, so only the first series of a new day
    /// reached the current state).
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
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Name) && double.IsFinite(x.Value))
            .ToList();
        if (series.Count == 0)
        {
            return;
        }

        await _sql.ExecuteAsync(async token =>
        {
            await using MySqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.Text;
            command.CommandText = "CALL sp_water_save_us(@mli, @state, @series)";
            command.Parameters.Add("@mli", MySqlDbType.VarChar, 64).Value = mli;
            command.Parameters.Add("@state", MySqlDbType.VarChar, 2).Value = state.Length > 2 ? state[..2] : state;
            command.Parameters.Add("@series", MySqlDbType.JSON).Value = BuildUsSeriesJson(series);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records one finished cycle in envdata's <c>job_run</c> (<c>sp_job_run_record</c>). env_service picks unhanded
    /// cycles up and does the fish hand-over to SQL Server (species push + probability update), which this service did
    /// itself until 10.8.0.
    /// </summary>
    public async Task RecordCycleAsync(
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

    /// <summary>
    /// Retention after each cycle (<c>sp_clean_old_water_data(NULL)</c>: raw readings 30 days by dropping whole day
    /// partitions, daily aggregates and job runs 30 days). Partition DDL commits implicitly, so this runs on its own
    /// connection, never inside a transaction. A no-op on most calls: only a new day adds or drops a partition.
    /// </summary>
    public async Task CleanOldWaterDataAsync(CancellationToken ct = default)
    {
        await _sql.ExecuteAsync(async token =>
        {
            await using MySqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.Text;
            command.CommandText = "CALL sp_clean_old_water_data(NULL)";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The <c>rows</c> JSON of <c>sp_water_save_ca</c>: <c>[{"s":"yyyy-MM-dd HH:mm:ss","e":level,"d":discharge}]</c>,
    /// time in UTC, a missing or non-finite value as <c>null</c>.
    /// </summary>
    internal static string BuildCaRowsJson(IEnumerable<Reading> readings)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (Reading reading in readings)
            {
                json.WriteStartObject();
                json.WriteString("s", reading.Stamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                WriteNumberOrNull(json, "e", reading.WaterLevel);
                WriteNumberOrNull(json, "d", reading.Discharge);
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The <c>series</c> JSON of <c>sp_water_save_us</c>:
    /// <c>[{"name":"Streamflow","unit":"ft^3/s","points":[{"d":"yyyy-MM-dd","v":12.5}]}]</c>, one point per series
    /// (the station's local day), grouped by name and unit.
    /// </summary>
    internal static string BuildUsSeriesJson(IEnumerable<UsSeriesReading> series)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (IGrouping<(string Name, string? Unit), UsSeriesReading> group in series.GroupBy(s => (s.Name, s.Unit)))
            {
                json.WriteStartObject();
                json.WriteString("name", group.Key.Name);
                if (group.Key.Unit is null)
                {
                    json.WriteNull("unit");
                }
                else
                {
                    json.WriteString("unit", group.Key.Unit);
                }
                json.WriteStartArray("points");
                foreach (UsSeriesReading point in group)
                {
                    json.WriteStartObject();
                    json.WriteString("d", point.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    json.WriteNumber("v", point.Value);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteNumberOrNull(Utf8JsonWriter json, string name, double? value)
    {
        if (value is double v && double.IsFinite(v))
        {
            json.WriteNumber(name, v);
        }
        else
        {
            json.WriteNull(name);
        }
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
