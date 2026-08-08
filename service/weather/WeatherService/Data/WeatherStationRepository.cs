using System.Data;
using WeatherService.Domain;

namespace WeatherService.Data;

/// <summary>
/// Loads the weather stations that should be processed, from <c>dbo.vwWeatherForecastToDay</c>.
/// </summary>
public class WeatherStationRepository
{
    private const int DefaultStationLimit = 1400;
    private const int UsWeatherGovStationLimit = 900;

    private const string FindSupportedStationsSql = """
        SELECT TOP (@limit) mli, lat, lon, state
        FROM dbo.vwWeatherForecastToDay
        WHERE country = @country
        """;

    private const string CountSupportedStationsSql = """
        SELECT COUNT(1)
        FROM dbo.vwWeatherForecastToDay
        WHERE country = @country
        """;

    private readonly ISqlConnectionFactory _connectionFactory;

    public WeatherStationRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <summary>US stations, capped at the Weather.gov station budget.</summary>
    public Task<IReadOnlyList<StationRef>> FindSupportedUsStationsAsync(CancellationToken ct = default) =>
        FindSupportedStationsAsync("US", UsWeatherGovStationLimit, ct);

    /// <summary>Stations for one country, capped at the default limit.</summary>
    public Task<IReadOnlyList<StationRef>> FindSupportedStationsAsync(
        string country, CancellationToken ct = default) =>
        FindSupportedStationsAsync(country, DefaultStationLimit, ct);

    /// <summary>Stations for one country, capped at <paramref name="limit"/>.</summary>
    public virtual async Task<IReadOnlyList<StationRef>> FindSupportedStationsAsync(
        string country, int limit, CancellationToken ct = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be positive");
        }

        var stations = new List<StationRef>();

        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandText = FindSupportedStationsSql;
        command.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        command.Parameters.Add("@country", SqlDbType.NVarChar, 8).Value = country;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        int mliOrdinal = reader.GetOrdinal("mli");
        int latOrdinal = reader.GetOrdinal("lat");
        int lonOrdinal = reader.GetOrdinal("lon");
        int stateOrdinal = reader.GetOrdinal("state");

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            stations.Add(new StationRef(
                reader.IsDBNull(mliOrdinal) ? string.Empty : reader.GetString(mliOrdinal),
                reader.IsDBNull(latOrdinal) ? 0d : Convert.ToDouble(reader.GetValue(latOrdinal)),
                reader.IsDBNull(lonOrdinal) ? 0d : Convert.ToDouble(reader.GetValue(lonOrdinal)),
                reader.IsDBNull(stateOrdinal) ? string.Empty : reader.GetString(stateOrdinal)));
        }

        return stations;
    }

    /// <summary>
    /// How many stations the view holds for a country, independent of any per-cycle limit. Used to
    /// size the daily API reservation before any station is loaded.
    /// </summary>
    public virtual async Task<int> CountSupportedStationsAsync(string country, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandText = CountSupportedStationsSql;
        command.Parameters.Add("@country", SqlDbType.NVarChar, 8).Value = country;

        object? count = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return count is null or DBNull ? 0 : Convert.ToInt32(count);
    }
}
