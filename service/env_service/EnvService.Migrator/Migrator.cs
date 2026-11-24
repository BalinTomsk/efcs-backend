using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using MySqlConnector;

namespace EnvService.Migrator;

/// <summary>
/// The one-off copy of MSSQL water/weather data into MySQL envdata (env_service plan step 3). READS SQL Server only
/// (plain SELECTs with NOLOCK, so the live writers are never blocked) and WRITES envdata only through its procedures:
/// <c>sp_station_mirror</c>, <c>sp_water_backfill</c>, <c>sp_weather_save</c>, <c>sp_fill_water_reading_day</c>,
/// <c>sp_fill_flow_state</c>, <c>sp_fill_pressure_state</c>. Every step is idempotent: re-running it upserts again.
/// </summary>
internal sealed class Migrator(string mssql, string mysql, MigratorOptions options, TextWriter log)
{
    private const int StationBatch = 1000;
    private const int WaterBatch = 2000;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        DateTime since = DateTime.UtcNow.Date.AddDays(-options.Days);
        log.WriteLine($"EnvService.Migrator: days={options.Days} since={since:yyyy-MM-dd} steps={string.Join(",", options.Steps)}"
                      + (options.DryRun ? " DRY RUN (reads SQL Server only, writes nothing)" : ""));

        if (options.Steps.Contains("stations"))
        {
            await StationsAsync(ct);
        }
        if (options.Steps.Contains("water"))
        {
            await WaterAsync(since, ct);
        }
        if (options.Steps.Contains("weather"))
        {
            if (!options.DryRun && !options.ForceWeather && await MySqlScalarAsync<long>("SELECT COUNT(*) FROM v_station_day", ct) > 0)
            {
                log.WriteLine("weather: SKIPPED - envdata already holds weather (weather 11.0.0 running?); --force-weather "
                              + "would overwrite newer payloads with the SQL Server copy");
            }
            else
            {
                await WeatherAsync(since, ct);
            }
        }
        if (options.Steps.Contains("fills") && !options.DryRun)
        {
            await FillsAsync(since, ct);
        }
        log.WriteLine("EnvService.Migrator: done");
        return 0;
    }

    private async Task StationsAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        const string sql = """
            SELECT w.sid, w.MLI, w.id, w.country, w.state, w.lat, w.lon, w.tz, w.locType, w.locName, w.lakeId, w.lakeName,
                   w.elevation, w.agency,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.lake_fish f WITH (NOLOCK) WHERE f.lake_Id = w.lakeId) THEN 1 ELSE 0 END AS bit),
                   w.supported, w.stamp
              FROM dbo.WaterStation w WITH (NOLOCK)
             ORDER BY w.sid
            """;
        var batch = new List<Dictionary<string, object?>>(StationBatch);
        int read = 0, mirrored = 0, skipped = 0;
        await using var source = new SqlConnection(mssql);
        await source.OpenAsync(ct);
        await using var command = new SqlCommand(sql, source) { CommandTimeout = 600 };
        await using SqlDataReader r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            read++;
            // The live WaterStation allows NULL in lat, lon, country and locName (the script says NOT NULL); envdata
            // needs a coordinate and a country, so such a station is skipped and counted.
            if (r.IsDBNull(3) || r.IsDBNull(5) || r.IsDBNull(6) || r.IsDBNull(1))
            {
                skipped++;
                continue;
            }
            var row = new StationRow(
                r.GetInt32(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetDouble(5), r.GetDouble(6), I(r, 7), I(r, 8) ?? 0, r.IsDBNull(9) ? string.Empty : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetGuid(10), r.IsDBNull(11) ? string.Empty : r.GetString(11), I(r, 12),
                r.IsDBNull(13) ? null : r.GetString(13), !r.IsDBNull(14) && r.GetBoolean(14),
                !r.IsDBNull(15) && r.GetBoolean(15), r.IsDBNull(16) ? DateTime.UtcNow : r.GetDateTime(16));
            batch.Add(Conversions.StationMirrorRow(row));
            if (batch.Count == StationBatch)
            {
                mirrored += await MirrorAsync(batch, ct);
            }
        }
        mirrored += await MirrorAsync(batch, ct);
        log.WriteLine($"stations: read={read} skippedNoCoordinateOrCountry={skipped} mirrored={mirrored} in {sw.Elapsed.TotalSeconds:F0}s");
    }

    private async Task<int> MirrorAsync(List<Dictionary<string, object?>> batch, CancellationToken ct)
    {
        if (batch.Count == 0 || options.DryRun)
        {
            int n = batch.Count;
            batch.Clear();
            return options.DryRun ? 0 : n;
        }
        string json = JsonSerializer.Serialize(batch);
        batch.Clear();
        return await CallOutAsync("CALL sp_station_mirror(@rows, @out_n); SELECT @out_n", "@rows", json, ct);
    }

    private async Task WaterAsync(DateTime since, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        const string sql = """
            SELECT w.sid, d.stamp, d.discharge, d.elevation, CAST(d.turbidity AS float), d.oxygen, CAST(d.ph AS int),
                   CAST(d.velocity AS float), CAST(d.pressure AS int), CAST(d.precipitation AS float), CAST(d.wind AS float),
                   CAST(d.winddir AS float), CAST(d.humidity AS float), d.Phycocyanins, d.Chlorophylls, d.nitrate,
                   d.chloride, d.phycoerythrin, d.salinity
              FROM dbo.WaterData d WITH (NOLOCK)
              JOIN dbo.WaterStation w WITH (NOLOCK) ON w.MLI = d.mli
             WHERE d.stamp >= @since
             ORDER BY w.sid, d.stamp
            """;
        var batch = new List<Dictionary<string, object>>(WaterBatch);
        long read = 0, empty = 0, stored = 0, nextReport = 500_000;
        await using var source = new SqlConnection(mssql);
        await source.OpenAsync(ct);
        await using var command = new SqlCommand(sql, source) { CommandTimeout = 0 };
        command.Parameters.Add("@since", SqlDbType.DateTime2).Value = since;
        await using SqlDataReader r = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await r.ReadAsync(ct))
        {
            var row = new WaterDataRow(
                r.GetInt32(0), r.GetDateTime(1), D(r, 2), D(r, 3), D(r, 4), D(r, 5), r.IsDBNull(6) ? null : r.GetInt32(6),
                D(r, 7), r.IsDBNull(8) ? null : r.GetInt32(8), D(r, 9), D(r, 10), D(r, 11), D(r, 12), D(r, 13), D(r, 14),
                D(r, 15), D(r, 16), D(r, 17), D(r, 18));
            read++;
            Dictionary<string, object>? element = Conversions.WaterBackfillRow(row);
            if (element is null)
            {
                empty++;
                continue;
            }
            batch.Add(element);
            if (batch.Count == WaterBatch)
            {
                stored += await BackfillAsync(batch, ct);
                if (read >= nextReport)
                {
                    log.WriteLine($"water: read={read} stored={stored} ({sw.Elapsed.TotalSeconds:F0}s)");
                    nextReport += 500_000;
                }
            }
        }
        stored += await BackfillAsync(batch, ct);
        log.WriteLine($"water: read={read} emptyRows={empty} stored={stored} in {sw.Elapsed.TotalSeconds:F0}s");
    }

    private async Task<int> BackfillAsync(List<Dictionary<string, object>> batch, CancellationToken ct)
    {
        if (batch.Count == 0 || options.DryRun)
        {
            batch.Clear();
            return 0;
        }
        string json = JsonSerializer.Serialize(batch);
        batch.Clear();
        return await CallOutAsync("CALL sp_water_backfill(@rows, @out_n); SELECT @out_n", "@rows", json, ct);
    }

    private async Task WeatherAsync(DateTime since, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        const string sql = """
            SELECT f.mli, ISNULL(o.type, 2), f.dt, f.tm, f.tmHigh, f.tmLow, f.tmDay, f.air_temperature, f.gpfDay, f.gpfNight,
                   f.humidity, f.wind_max_speed, f.wind_degree, f.wind_direction, f.pressure, f.pop, f.rain_today,
                   f.weather_code, f.icon, f.shortText, f.longText
              FROM dbo.weather_Forecast f WITH (NOLOCK)
              JOIN dbo.WaterStation w WITH (NOLOCK) ON w.MLI = f.mli
              LEFT JOIN dbo.ows_meteo o WITH (NOLOCK) ON o.mli = f.mli
             WHERE f.dt >= @since
             ORDER BY f.mli, f.dt
            """;
        int stations = 0, days = 0, unknown = 0, notShredded = 0;
        string? current = null;
        int currentType = 2;
        var rows = new List<ForecastRow>();
        await using var source = new SqlConnection(mssql);
        await source.OpenAsync(ct);
        await using var command = new SqlCommand(sql, source) { CommandTimeout = 600 };
        command.Parameters.Add("@since", SqlDbType.Date).Value = since;
        await using SqlDataReader r = await command.ExecuteReaderAsync(ct);

        async Task FlushAsync()
        {
            if (current is null || rows.Count == 0)
            {
                return;
            }
            stations++;
            if (!options.DryRun)
            {
                string json = Conversions.ForecastEnvelope(current, currentType, rows, DateTimeOffset.UtcNow);
                try
                {
                    int n = await CallWeatherAsync(current, currentType, json, ct);
                    if (n < 0)
                    {
                        notShredded++;
                    }
                    else
                    {
                        days += n;
                    }
                }
                catch (MySqlException ex) when (ex.SqlState == "45000")
                {
                    unknown++;
                }
            }
            else
            {
                days += rows.Count;
            }
            rows.Clear();
        }

        while (await r.ReadAsync(ct))
        {
            string mli = r.GetString(0);
            if (mli != current)
            {
                await FlushAsync();
                current = mli;
                currentType = r.GetInt32(1);
            }
            rows.Add(new ForecastRow(
                mli, currentType, DateOnly.FromDateTime(r.GetDateTime(2)), r.IsDBNull(3) ? null : r.GetTimeSpan(3),
                D(r, 4), D(r, 5), D(r, 6), I(r, 7), D(r, 8), D(r, 9), D(r, 10), D(r, 11), D(r, 12),
                r.IsDBNull(13) ? null : r.GetString(13), I(r, 14), I(r, 15), I(r, 16), I(r, 17),
                r.IsDBNull(18) ? null : r.GetString(18), r.IsDBNull(19) ? null : r.GetString(19),
                r.IsDBNull(20) ? null : r.GetString(20)));
        }
        await FlushAsync();
        log.WriteLine($"weather: stations={stations} days={days} unknownStations={unknown} notShredded={notShredded} "
                      + $"in {sw.Elapsed.TotalSeconds:F0}s");
    }

    private async Task FillsAsync(DateTime since, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await using var connection = new MySqlConnection(mysql);
        await connection.OpenAsync(ct);
        async Task<object?> Run(string sql)
        {
            await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 0 };
            command.Parameters.Add("@from", MySqlDbType.Date).Value = since;
            return await command.ExecuteScalarAsync(ct);
        }

        object? daysN = await Run("CALL sp_fill_water_reading_day(@from, UTC_DATE(), NULL, @out_n); SELECT @out_n");
        log.WriteLine($"fills: water_reading_day rows={daysN} ({sw.Elapsed.TotalSeconds:F0}s)");
        object? flowN = await Run("CALL sp_fill_flow_state(NULL, NULL, 0, @out_n); SELECT @out_n");
        log.WriteLine($"fills: flow states={flowN} ({sw.Elapsed.TotalSeconds:F0}s)");
        object? pressureN = await Run("CALL sp_fill_pressure_state(NULL, NULL, 0, NULL, @out_n); SELECT @out_n");
        log.WriteLine($"fills: pressure rows={pressureN} in {sw.Elapsed.TotalSeconds:F0}s");
    }

    private async Task<int> CallOutAsync(string sql, string name, string json, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(mysql);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 600 };
        command.Parameters.Add(name, MySqlDbType.JSON).Value = json;
        object? result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    private async Task<int> CallWeatherAsync(string mli, int type, string json, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(mysql);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(
            "CALL sp_weather_save(@mli, @type, @json, @out_days); SELECT @out_days", connection) { CommandTimeout = 120 };
        command.Parameters.Add("@mli", MySqlDbType.VarChar, 64).Value = mli;
        command.Parameters.Add("@type", MySqlDbType.Byte).Value = (sbyte)type;
        command.Parameters.Add("@json", MySqlDbType.MediumText).Value = json;
        object? result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? -1 : Convert.ToInt32(result);
    }

    private async Task<T> MySqlScalarAsync<T>(string sql, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(mysql);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync(ct))!, typeof(T));
    }

    private static double? D(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i));

    private static int? I(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));
}

internal sealed record MigratorOptions(int Days, IReadOnlySet<string> Steps, bool DryRun, bool ForceWeather);
