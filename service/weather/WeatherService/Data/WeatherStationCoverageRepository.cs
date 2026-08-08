using System.Data;
using Microsoft.Data.SqlClient;
using Polly;
using Polly.Registry;
using WeatherService.Configuration;

namespace WeatherService.Data;

/// <summary>
/// Records whether a provider can serve a given gauge, so a fallback worker can pick up the ones it
/// cannot (<c>dbo.weather_station_coverage</c>, via <c>dbo.sp_save_weather_station_coverage</c> —
/// never the table directly).
///
/// <para>Not every provider answers every coordinate. Weather Canada's SWOB is an observation network
/// with real geographic gaps — even a 0.5° (~55 km) search box finds nothing for roughly one Canadian
/// gauge in six. Those gauges otherwise skip silently on every cycle forever, and a fully-skipped
/// cycle still reports healthy, so nothing surfaces them.</para>
/// </summary>
public class WeatherStationCoverageRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ResiliencePipeline _sql;

    public WeatherStationCoverageRepository(
        ISqlConnectionFactory connectionFactory,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _connectionFactory = connectionFactory;
        _sql = pipelineProvider.GetPipeline(ResiliencePipelines.Sql);
    }

    /// <summary>
    /// Flags whether <paramref name="provider"/> had data for <paramref name="mli"/>. Upserts, so a gap
    /// that later resolves simply clears.
    /// </summary>
    public virtual async Task SaveAsync(
        string mli, string provider, bool covered, CancellationToken ct = default) =>
        await _sql.ExecuteAsync(async token =>
        {
            await using SqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = "dbo.sp_save_weather_station_coverage";
            command.Parameters.Add("@mli", SqlDbType.VarChar, 64).Value = mli;
            command.Parameters.Add("@provider", SqlDbType.VarChar, 32).Value = provider;
            command.Parameters.Add("@covered", SqlDbType.Bit).Value = covered;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
}
