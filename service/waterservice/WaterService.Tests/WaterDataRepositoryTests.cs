using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Polly.Registry;
using TUnit.Core;
using WaterService.Configuration;
using WaterService.Data;
using WaterService.Domain;

namespace WaterService.Tests;

/// <summary>
/// Pins the JSON the envdata procedures expand with JSON_TABLE (<c>sp_water_save_ca</c> rows,
/// <c>sp_water_save_us</c> series) and the SQL failure rule. The procedures themselves are tested in
/// <c>envfish-db/mysql/envdata/UNIT_TESTS</c>; <see cref="WaterEngineTests"/> runs this repository against a real engine.
/// </summary>
public class WaterDataRepositoryTests
{
    [Test]
    public async Task CaRowsJson_HasUtcTimeLevelAndDischarge()
    {
        string json = WaterDataRepository.BuildCaRowsJson(
        [
            new Reading("02HB004", new DateTimeOffset(2026, 11, 24, 5, 5, 0, TimeSpan.FromHours(-5)), 1.234, 56.7),
        ]);

        await Assert.That(json).IsEqualTo("[{\"s\":\"2026-11-24 10:05:00\",\"e\":1.234,\"d\":56.7}]");
    }

    [Test]
    public async Task CaRowsJson_WritesMissingAndNonFiniteValuesAsNull()
    {
        string json = WaterDataRepository.BuildCaRowsJson(
        [
            new Reading("x", new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero), null, double.NaN),
        ]);

        await Assert.That(json).IsEqualTo("[{\"s\":\"2026-11-24 10:00:00\",\"e\":null,\"d\":null}]");
    }

    [Test]
    public async Task UsSeriesJson_GroupsByNameAndUnit_OnePointPerReading()
    {
        string json = WaterDataRepository.BuildUsSeriesJson(
        [
            new UsSeriesReading("Streamflow", "ft^3/s", new DateOnly(2026, 11, 23), 12.5),
            new UsSeriesReading("pH", null, new DateOnly(2026, 11, 23), 7.62),
            new UsSeriesReading("Streamflow", "ft^3/s", new DateOnly(2026, 11, 24), 13),
        ]);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        await Assert.That(root.GetArrayLength()).IsEqualTo(2);
        await Assert.That(root[0].GetProperty("name").GetString()).IsEqualTo("Streamflow");
        await Assert.That(root[0].GetProperty("unit").GetString()).IsEqualTo("ft^3/s");
        await Assert.That(root[0].GetProperty("points").GetArrayLength()).IsEqualTo(2);
        await Assert.That(root[0].GetProperty("points")[0].GetProperty("d").GetString()).IsEqualTo("2026-11-23");
        await Assert.That(root[0].GetProperty("points")[0].GetProperty("v").GetDouble()).IsEqualTo(12.5);
        await Assert.That(root[1].GetProperty("unit").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task SqlFailure_AProcedureRefusalIsNotRetriedNorCountedByTheBreaker()
    {
        // SQLSTATE 45000 = an envdata procedure's own SIGNAL (e.g. unknown station mli): a bad request, not an outage.
        await Assert.That(ResiliencePipelines.IsSqlFailure(new TimeoutException())).IsTrue();
        await Assert.That(ResiliencePipelines.IsSqlFailure(new InvalidOperationException())).IsFalse();
    }
}

/// <summary>
/// The real repositories against a real MySQL engine, so every CALL is parsed and bound the way production does it (a
/// fake never parses SQL). Runs only when <c>WATER_TEST_MYSQL</c> holds a JDBC URL or connection string of a THROWAWAY
/// database built from <c>envfish-db/mysql/envdata</c> (script0 + script01_* + script02_Proc) — it writes rows and
/// deletes them again. Otherwise each test is a no-op.
/// <code>WATER_TEST_MYSQL="Server=127.0.0.1;Port=3306;Database=envtest;User ID=root;SslMode=None"</code>
/// </summary>
[NotInParallel]
public class WaterEngineTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("WATER_TEST_MYSQL");
    private const int Sid = 990001;
    private const string Mli = "ENGINE-TEST-1";

    private static string ConnectionString() => MySqlUrl.Build(Url!, null, null);

    private static async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new MySqlConnection(ConnectionString());
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        object? value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    private static async Task ExecAsync(string sql)
    {
        await using var connection = new MySqlConnection(ConnectionString());
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static Task CleanAsync() => ExecAsync(
        $"DELETE FROM water_reading WHERE station_id = {Sid}; DELETE FROM job_run WHERE job = 'engine-test'; " +
        $"DELETE FROM station WHERE station_id = {Sid};");

    private static Task SeedStationAsync(string country) => ExecAsync(
        "INSERT INTO station (station_id, mli, ws_id, country, state, lat, lon, loc_name, master_stamp, mirror_stamp) " +
        $"VALUES ({Sid}, '{Mli}', UUID(), '{country}', 'ON', 45, -79, 'engine test', UTC_TIMESTAMP(), UTC_TIMESTAMP())");

    private static (WaterDataRepository Data, StationHttp503BackoffRepository Backoff, WaterStationRepository Stations) Repositories()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWaterResiliencePipelines();
        ResiliencePipelineProvider<string> pipelines =
            services.BuildServiceProvider().GetRequiredService<ResiliencePipelineProvider<string>>();
        var factory = new SqlConnectionFactory(ConnectionString());
        return (new WaterDataRepository(factory, pipelines), new StationHttp503BackoffRepository(factory, pipelines),
            new WaterStationRepository(factory));
    }

    [Test]
    public async Task CaSave_WritesReadingsAndCurrentState_InOneTransaction()
    {
        if (Url is null)
        {
            return;
        }
        await CleanAsync();
        await SeedStationAsync("CA");
        try
        {
            var (data, _, stations) = Repositories();
            var t = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
            List<Reading> readings = Enumerable.Range(0, 700)
                .Select(i => new Reading(Mli, t.AddMinutes(-5 * i), 1.5, i == 0 ? -999 : 20.0 + i))
                .ToList();

            await data.SaveStationDataAsync(Mli, readings);

            await Assert.That(await ScalarAsync<long>($"SELECT COUNT(*) FROM water_reading WHERE station_id = {Sid}")).IsEqualTo(700L);
            await Assert.That(await ScalarAsync<double>($"SELECT discharge FROM station_current WHERE station_id = {Sid}")).IsEqualTo(21.0);
            IReadOnlyList<StationRef> due = await stations.FindSupportedAsync("CA");
            await Assert.That(due.Any(s => s.Mli == Mli)).IsTrue();
        }
        finally
        {
            await CleanAsync();
        }
    }

    [Test]
    public async Task UsSave_MergesAllSeriesOfADay_InMetricAndMmHg()
    {
        if (Url is null)
        {
            return;
        }
        await CleanAsync();
        await SeedStationAsync("US");
        try
        {
            var (data, _, _) = Repositories();
            var day = new DateOnly(2026, 11, 23);

            await data.SaveUsStationDataAsync(Mli, "WI",
            [
                new UsSeriesReading("Streamflow", "ft^3/s", day, 100),
                new UsSeriesReading("Barometric pressure", "mmHg", day, 755.34),
                new UsSeriesReading("pH", "water, unfiltered, field, standard units", day, 7.62),
            ]);

            await Assert.That(await ScalarAsync<string>(
                $"SELECT CONCAT(ROUND(discharge, 4), '|', pressure_mm, '|', ph) FROM water_reading WHERE station_id = {Sid}"))
                .IsEqualTo("2.8317|755.3|7.62");
            await Assert.That(await ScalarAsync<string>($"SELECT state FROM station WHERE station_id = {Sid}")).IsEqualTo("WI");
        }
        finally
        {
            await CleanAsync();
        }
    }

    [Test]
    public async Task UnknownStation_IsRefusedWith45000()
    {
        if (Url is null)
        {
            return;
        }
        var (data, _, _) = Repositories();
        MySqlException? refused = null;
        try
        {
            await data.SaveStationDataAsync("NO-SUCH-STATION",
                [new Reading("x", DateTimeOffset.UtcNow, 1, 1)]);
        }
        catch (MySqlException ex)
        {
            refused = ex;
        }

        await Assert.That(refused).IsNotNull();
        await Assert.That(refused!.SqlState).IsEqualTo("45000");
        await Assert.That(ResiliencePipelines.IsSqlFailure(refused)).IsFalse();
    }

    [Test]
    public async Task Backoff_JobRun_AndCleanup_RunOnTheEngine()
    {
        if (Url is null)
        {
            return;
        }
        await CleanAsync();
        await SeedStationAsync("CA");
        try
        {
            var (data, backoff, _) = Repositories();
            var today = new DateOnly(2026, 11, 22);
            await backoff.RecordHttp503Async("ec", "CA", Mli, "ON", today.AddDays(-2));
            await backoff.RecordHttp503Async("ec", "CA", Mli, "ON", today.AddDays(-1));
            await backoff.RecordHttp503Async("ec", "CA", Mli, "ON", today);
            await Assert.That(await ScalarAsync<int>($"SELECT backoff_state FROM station WHERE station_id = {Sid}")).IsEqualTo(2);
            IReadOnlyList<BackoffSummary> summary = await backoff.SummaryByStateAsync();
            await Assert.That(summary.Any(s => s.State == "ON" && s.BackoffStage == "WEEKLY")).IsTrue();
            await backoff.ResetAsync("ec", "CA", Mli);
            await backoff.RefreshDueAsync(today);
            await Assert.That(await ScalarAsync<int>($"SELECT supported FROM station WHERE station_id = {Sid}")).IsEqualTo(1);

            await data.RecordCycleAsync("engine-test", DateTime.UtcNow.AddMinutes(-3), DateTime.UtcNow, "ok", 5, null);
            await Assert.That(await ScalarAsync<long>(
                "SELECT COUNT(*) FROM v_job_run_pending WHERE job = 'engine-test' AND items = 5")).IsEqualTo(1L);

            await data.CleanOldWaterDataAsync();
            await Assert.That(await ScalarAsync<long>(
                "SELECT COUNT(*) FROM information_schema.partitions WHERE table_schema = DATABASE() " +
                "AND table_name = 'water_reading' AND partition_name LIKE 'p\\_2%'")).IsGreaterThan(30L);
        }
        finally
        {
            await CleanAsync();
        }
    }
}
