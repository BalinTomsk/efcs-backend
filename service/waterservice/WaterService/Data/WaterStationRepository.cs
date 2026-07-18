using System.Data;
using WaterService.Domain;

namespace WaterService.Data;

/// <summary>
/// Loads station metadata from the <c>vwWaterStation</c> view.
/// </summary>
public sealed class WaterStationRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public WaterStationRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Returns all supported stations for the requested country that should be processed by the worker.
    /// </summary>
    /// <param name="country">Country code used to filter stations (<c>CA</c> / <c>US</c>).</param>
    public async Task<IReadOnlyList<StationRef>> FindSupportedAsync(
        string country, CancellationToken cancellationToken = default)
    {
        var stations = new List<StationRef>();

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandText =
            "SELECT mli, state, tz FROM vwWaterStation WHERE country = @country ORDER BY stamp DESC";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@country";
        parameter.Value = country;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        int mliOrdinal = reader.GetOrdinal("mli");
        int stateOrdinal = reader.GetOrdinal("state");
        int tzOrdinal = reader.GetOrdinal("tz");

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string mli = reader.IsDBNull(mliOrdinal) ? string.Empty : reader.GetString(mliOrdinal);
            string state = reader.IsDBNull(stateOrdinal) ? string.Empty : reader.GetString(stateOrdinal);
            int tz = reader.IsDBNull(tzOrdinal) ? 0 : Convert.ToInt32(reader.GetValue(tzOrdinal));
            stations.Add(new StationRef(mli, state, tz));
        }

        return stations;
    }
}
