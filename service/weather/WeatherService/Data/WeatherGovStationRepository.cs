using System.Data;
using MySqlConnector;
using Polly;
using Polly.Registry;
using WeatherService.Configuration;

namespace WeatherService.Data;

/// <summary>
/// Reads and writes the cached "which NWS station serves this water gauge" mapping
/// (envdata <c>weather_gov_station</c>), via the view <c>v_weather_gov_station</c> and
/// <c>sp_weather_gov_station_save</c> — never the table directly.
/// </summary>
public class WeatherGovStationRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ResiliencePipeline _sql;

    public WeatherGovStationRepository(
        ISqlConnectionFactory connectionFactory,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _connectionFactory = connectionFactory;
        _sql = pipelineProvider.GetPipeline(ResiliencePipelines.Sql);
    }

    /// <summary>
    /// The cached answer for a gauge, or <c>null</c> when it has never been resolved.
    ///
    /// <para>Note the two distinct "empty" cases: <c>null</c> here means *never asked*, while a
    /// returned value whose <c>StationId</c> is <c>null</c> means *asked, and there is no station
    /// nearby* — a negative cache that stops the resolver re-asking a point that will never resolve.</para>
    /// </summary>
    public virtual async Task<CachedStation?> FindAsync(string mli, CancellationToken ct = default) =>
        await _sql.ExecuteAsync(async token =>
        {
            await using MySqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.Text;
            command.CommandText = "SELECT station_id FROM v_weather_gov_station WHERE mli = @mli";
            command.Parameters.Add("@mli", MySqlDbType.VarChar, 64).Value = mli;

            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return (CachedStation?)null;
            }

            return new CachedStation(reader.IsDBNull(0) ? null : reader.GetString(0));
        }, ct).ConfigureAwait(false);

    /// <summary>
    /// Records the resolution for a gauge. Pass <paramref name="stationId"/> as <c>null</c> to cache
    /// "no station nearby".
    /// </summary>
    public virtual async Task SaveAsync(
        string mli, double latitude, double longitude, string? stationId, CancellationToken ct = default) =>
        await _sql.ExecuteAsync(async token =>
        {
            await using MySqlConnection connection = await _connectionFactory.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.Text;
            command.CommandText = "CALL sp_weather_gov_station_save(@mli, @lat, @lon, @station_id)";
            command.Parameters.Add("@mli", MySqlDbType.VarChar, 64).Value = mli;
            command.Parameters.Add("@lat", MySqlDbType.Double).Value = latitude;
            command.Parameters.Add("@lon", MySqlDbType.Double).Value = longitude;
            command.Parameters.Add("@station_id", MySqlDbType.VarChar, 16).Value = (object?)stationId ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    /// <param name="StationId">The NWS call sign, or <c>null</c> for "resolved, none nearby".</param>
    public readonly record struct CachedStation(string? StationId);
}
