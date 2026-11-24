using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Polly.Registry;
using TUnit.Core;
using WeatherService.Canonical;
using WeatherService.Configuration;
using WeatherService.Data;

namespace WeatherService.Tests;

/// <summary>
/// The real repositories against a real MySQL engine, so every CALL is parsed and bound the way production does it (a
/// fake never parses SQL), with payloads serialized by the real <see cref="CanonicalForecast"/>. Runs only when
/// <c>WEATHER_TEST_MYSQL</c> holds a JDBC URL or connection string of a THROWAWAY database built from
/// <c>envfish-db/mysql/envdata</c> (script0 + script01_* + script02_Proc) — it writes rows and deletes them again.
/// Otherwise each test is a no-op.
/// <code>WEATHER_TEST_MYSQL="Server=127.0.0.1;Port=3306;Database=envtest;User ID=root;SslMode=None"</code>
/// </summary>
[NotInParallel]
public class WeatherEngineTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("WEATHER_TEST_MYSQL");
    private const int Sid = 990101;
    private const string Mli = "ENGINE-WX-1";

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
        $"DELETE FROM job_run WHERE job = 'engine-test'; DELETE FROM station WHERE station_id = {Sid};");

    private static Task SeedStationAsync() => ExecAsync(
        "INSERT INTO station (station_id, mli, ws_id, country, state, lat, lon, loc_name, master_stamp, mirror_stamp, " +
        $"has_fish) VALUES ({Sid}, '{Mli}', UUID(), 'CA', 'ON', 45, -79, 'engine test', UTC_TIMESTAMP(), UTC_TIMESTAMP(), 1)");

    private static ResiliencePipelineProvider<string> Pipelines()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWeatherResiliencePipelines();
        return services.BuildServiceProvider().GetRequiredService<ResiliencePipelineProvider<string>>();
    }

    [Test]
    public async Task CanonicalPayload_IsStoredAndShredded_WithPressureInMmHg()
    {
        if (Url is null)
        {
            return;
        }
        await CleanAsync();
        await SeedStationAsync();
        try
        {
            var factory = new SqlConnectionFactory(ConnectionString());
            var data = new WeatherDataRepository(factory, Pipelines(), NullLogger<WeatherDataRepository>.Instance);
            var forecast = new CanonicalForecast
            {
                Provider = "open-meteo",
                ProviderType = WeatherSourceType.OpenMeteo,
                Mli = Mli,
                FetchedUtc = DateTimeOffset.UtcNow,
                Days =
                [
                    new ForecastDay { Date = new DateOnly(2026, 11, 24), Time = "12:00:00", TempHighC = 5.5, PressureHpa = 1013 },
                    new ForecastDay { Date = new DateOnly(2026, 11, 25), Time = "12:00:00", TempHighC = 3 },
                ],
            };

            await data.SaveStationDataAsync(Mli, forecast.ToJson(), WeatherSourceType.OpenMeteo);

            await Assert.That(await ScalarAsync<long>($"SELECT COUNT(*) FROM weather_day WHERE station_id = {Sid}")).IsEqualTo(2L);
            await Assert.That(await ScalarAsync<decimal>(
                $"SELECT pressure_mm FROM weather_day WHERE station_id = {Sid} AND dt = '2026-11-24'")).IsEqualTo(759.8m);
            await Assert.That(await ScalarAsync<long>(
                $"SELECT COUNT(*) FROM weather_raw WHERE station_id = {Sid} AND provider_type = 2")).IsEqualTo(1L);
        }
        finally
        {
            await CleanAsync();
        }
    }

    [Test]
    public async Task UnknownStation_IsLoggedAndDropped_NotAFailure()
    {
        if (Url is null)
        {
            return;
        }
        var factory = new SqlConnectionFactory(ConnectionString());
        var data = new WeatherDataRepository(factory, Pipelines(), NullLogger<WeatherDataRepository>.Instance);

        await data.SaveStationDataAsync("NO-SUCH-STATION", "{}", WeatherSourceType.OpenMeteo);
    }

    [Test]
    public async Task StationList_NwsCache_Coverage_JobRun_AndCleanup_RunOnTheEngine()
    {
        if (Url is null)
        {
            return;
        }
        await CleanAsync();
        await SeedStationAsync();
        try
        {
            var factory = new SqlConnectionFactory(ConnectionString());
            ResiliencePipelineProvider<string> pipelines = Pipelines();
            var stations = new WeatherStationRepository(factory);
            var nws = new WeatherGovStationRepository(factory, pipelines);
            var coverage = new WeatherStationCoverageRepository(factory, pipelines);
            var data = new WeatherDataRepository(factory, pipelines, NullLogger<WeatherDataRepository>.Instance);

            await Assert.That(await stations.CountSupportedStationsAsync("CA")).IsGreaterThanOrEqualTo(1);
            var due = await stations.FindSupportedStationsAsync("CA", 100000);
            await Assert.That(due.Any(s => s.Mli == Mli)).IsTrue();

            await Assert.That(await nws.FindAsync(Mli)).IsNull();
            await nws.SaveAsync(Mli, 45, -79, null);
            await Assert.That((await nws.FindAsync(Mli))!.Value.StationId).IsNull();
            await nws.SaveAsync(Mli, 45, -79, "KMKE");
            await Assert.That((await nws.FindAsync(Mli))!.Value.StationId).IsEqualTo("KMKE");

            await coverage.SaveAsync(Mli, "weather-canada", false);
            await coverage.SaveAsync(Mli, "weather-canada", true);
            await Assert.That(await ScalarAsync<int>(
                $"SELECT covered FROM provider_coverage WHERE station_id = {Sid} AND provider = 'weather-canada'")).IsEqualTo(1);

            await data.RecordCycleAsync("engine-test", DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow, "ok", 7, null);
            await Assert.That(await ScalarAsync<long>(
                "SELECT COUNT(*) FROM v_job_run_pending WHERE job = 'engine-test' AND items = 7")).IsEqualTo(1L);
            await data.CleanOldWeatherDataAsync();
        }
        finally
        {
            await CleanAsync();
        }
    }
}
