using System.Data;
using System.Text.Json;
using EnvService.Configuration;
using EnvService.Data;
using Microsoft.Extensions.Configuration;
using TUnit.Core;

namespace EnvService.Tests;

/// <summary>The fixed shapes env_service shares with the databases and the port split.</summary>
public class ContractTests
{
    [Test]
    public async Task StateTable_ColumnsAreInTheOrderOfEnvStationStateRows()
    {
        // env.StationStateRows (envfish-db/mssql/script01_createTable.sql): a TVP binds by ORDINAL, not by name.
        DataTable t = SqlFishStore.StateTable([]);
        string[] names = t.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();

        await Assert.That(string.Join(",", names)).IsEqualTo(
            "sid,mli,ts,discharge,turbidity,oxygen,ph,elevation,flow_state,q_ratio_1d,q_ratio_2d,q_ratio_3_7d," +
            "q_median_30d,days_since_peak,flow_stamp");
    }

    [Test]
    public async Task DayTable_ColumnsAreInTheOrderOfEnvStationDayRows()
    {
        DataTable t = SqlFishStore.DayTable([]);
        string[] names = t.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();

        await Assert.That(string.Join(",", names)).IsEqualTo(
            "sid,dt,tm_day,tm_high,tm_low,pop,weather_code,pressure_mm,pressure_state,front_dt,swing_mm,strength," +
            "season,pressure_weight");
    }

    [Test]
    public async Task DayTable_WritesNullsAsDbNull()
    {
        DataTable t = SqlFishStore.DayTable(
            [new StationDayRow(1, new DateOnly(2026, 11, 25), null, null, null, null, null, null, null, null, null, null, null, null)]);

        await Assert.That(t.Rows[0]["pressure_mm"]).IsEqualTo(DBNull.Value);
        await Assert.That(t.Rows[0]["front_dt"]).IsEqualTo(DBNull.Value);
        await Assert.That(t.Rows[0]["dt"]).IsEqualTo(new DateTime(2026, 11, 25));
    }

    [Test]
    public async Task MirrorJson_HasTheNamesSpStationMirrorReads()
    {
        var s = new StationMaster(12, "02HB004", Guid.Empty, "CA ", " ", 43.5, -79.6, -5, 2, "Credit", null, "Credit River",
            null, null, true, false, new DateTime(2026, 1, 2, 3, 4, 5));

        string json = JsonSerializer.Serialize(StationMirrorJson.Of(s));

        await Assert.That(json).Contains("\"sid\":12").And.Contains("\"hasFish\":1").And.Contains("\"supported\":0")
            .And.Contains("\"country\":\"CA\"").And.Contains("\"state\":null").And.Contains("\"stamp\":\"2026-01-02 03:04:05\"");
    }

    [Test]
    public async Task AdminAndHealthPaths_AreManagementOnly()
    {
        await Assert.That(Program.IsManagementPath("/admin/jobs")).IsTrue();
        await Assert.That(Program.IsManagementPath("/admin/run/handover")).IsTrue();
        await Assert.That(Program.IsManagementPath("/metrics")).IsTrue();
        await Assert.That(Program.IsManagementPath("/health")).IsFalse();
        await Assert.That(Program.IsManagementPath("/api/v1/env/stations/map")).IsFalse();
    }

    [Test]
    public async Task SqlFailure_IgnoresAnEnvdataRefusal()
    {
        await Assert.That(ResiliencePipelines.IsSqlFailure(new TimeoutException())).IsTrue();
        await Assert.That(ResiliencePipelines.IsSqlFailure(new InvalidOperationException())).IsFalse();
    }

    [Test]
    public async Task Connections_RequireBothDatabases()
    {
        var onlyMySql = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MYSQL_URL"] = "Server=h;Database=envdata" }).Build();

        InvalidOperationException? thrown = null;
        try
        {
            ServiceRegistration.BuildConnections(onlyMySql);
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Message).Contains("DB_URL");
    }
}
