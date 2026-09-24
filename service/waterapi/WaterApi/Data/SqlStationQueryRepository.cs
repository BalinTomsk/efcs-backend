using System.Data;
using Microsoft.Data.SqlClient;
using WaterApi.Domain;

namespace WaterApi.Data;

/// <summary>
/// Reads map stations from SQL Server's <c>dbo.vMapView</c> — the same view the frontend's
/// <c>Editor/ViewMap.aspx.cs</c> queries directly today. The view already applies the map rules
/// (stations with a current reading, excluding HI/PR); this class adds none of its own.
///
/// <para>Called only by <see cref="Services.StationMapCache"/> on a refresh, never per HTTP request, so
/// the view's cost is paid once per country per refresh interval.</para>
/// </summary>
public class SqlStationQueryRepository : IStationQueryRepository
{
    /// <summary>
    /// Parameterized — unlike the legacy page, which concatenated <c>state</c> into the SQL text. The state
    /// filter is applied in memory against the cached snapshot instead.
    /// </summary>
    internal const string MapStationsSql =
        "SELECT sid, lat, lon, country, state, stamp FROM dbo.vMapView WHERE country = @country ORDER BY sid";

    private readonly ISqlConnectionFactory _connectionFactory;

    public SqlStationQueryRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public virtual async Task<IReadOnlyList<MapStation>> FindMapStationsAsync(
        string country, CancellationToken cancellationToken = default)
    {
        var stations = new List<MapStation>(capacity: 16_384);

        await using SqlConnection connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandText = MapStationsSql;
        command.Parameters.Add("@country", SqlDbType.Char, 3).Value = country;

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        int sidOrdinal = reader.GetOrdinal("sid");
        int latOrdinal = reader.GetOrdinal("lat");
        int lonOrdinal = reader.GetOrdinal("lon");
        int countryOrdinal = reader.GetOrdinal("country");
        int stateOrdinal = reader.GetOrdinal("state");
        int stampOrdinal = reader.GetOrdinal("stamp");

        // The view's UNION can in principle return one sid twice (two CurrentWaterState rows with different
        // stamps). The map needs one pin per station, so keep the newest.
        var seen = new Dictionary<int, int>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int sid = reader.GetInt32(sidOrdinal);
            DateTime stamp = reader.IsDBNull(stampOrdinal) ? DateTime.MinValue : reader.GetDateTime(stampOrdinal);
            var station = new MapStation(
                sid,
                MapStation.ToBase36((ulong)sid),
                reader.GetDouble(latOrdinal),
                reader.GetDouble(lonOrdinal),
                reader.IsDBNull(countryOrdinal) ? country : reader.GetString(countryOrdinal).Trim(),
                reader.IsDBNull(stateOrdinal) ? string.Empty : reader.GetString(stateOrdinal).Trim(),
                DateTime.SpecifyKind(stamp, DateTimeKind.Utc));

            if (seen.TryGetValue(sid, out int index))
            {
                if (station.Stamp > stations[index].Stamp)
                {
                    stations[index] = station;
                }
                continue;
            }

            seen[sid] = stations.Count;
            stations.Add(station);
        }

        return stations;
    }
}
