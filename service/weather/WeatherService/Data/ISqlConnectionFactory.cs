using MySqlConnector;

namespace WeatherService.Data;

/// <summary>
/// Opens ready-to-use connections to the MySQL <c>envdata</c> database (the water/weather store; since 11.0.0 this
/// service no longer talks to SQL Server at all).
/// </summary>
public interface ISqlConnectionFactory
{
    /// <summary>Opens a new connection asynchronously.</summary>
    Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken = default);
}
