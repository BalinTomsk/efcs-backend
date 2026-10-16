using TUnit.Core;
using WaterService.Data;

namespace WaterService.Tests;

/// <summary>
/// Pins the shape of the batched upsert. The SQL itself was validated against a real SQL Server (local
/// <c>envionx</c>, 700 readings in two chunks, update path, NULLs, rollback) — a mocked connection never
/// parses it.
/// </summary>
public class WaterDataRepositoryTests
{
    [Test]
    public async Task BuildExecBatch_EmitsOneParameterisedExecPerCall()
    {
        string sql = WaterDataRepository.BuildExecBatch("dbo.sp_UpdateWaterData", "@mli", 2, "@s", "@e", "@d");

        await Assert.That(sql).IsEqualTo(
            "EXEC dbo.sp_UpdateWaterData @mli, @s0, @e0, @d0;\n" +
            "EXEC dbo.sp_UpdateWaterData @mli, @s1, @e1, @d1;\n");
    }

    [Test]
    public async Task BuildExecBatch_KeepsSharedParametersFirst()
    {
        string sql = WaterDataRepository.BuildExecBatch("dbo.sp_push_us_water_data", "@mli, @state", 1, "@n", "@u", "@x");

        await Assert.That(sql).IsEqualTo("EXEC dbo.sp_push_us_water_data @mli, @state, @n0, @u0, @x0;\n");
    }

    [Test]
    public async Task MaxReadingsPerBatch_StaysUnderSqlServersParameterCap()
    {
        // SQL Server rejects a request with more than 2,100 parameters: three per reading plus @mli.
        await Assert.That(WaterDataRepository.MaxReadingsPerBatch * 3 + 1).IsLessThanOrEqualTo(2100);
    }
}
