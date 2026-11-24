using MySqlConnector;

namespace WaterService.Data;

/// <summary>
/// Default <see cref="ISqlConnectionFactory"/> that hands out fresh <see cref="MySqlConnection"/>s from a single
/// connection string. MySqlConnector pools the physical connections (the analogue of the Java HikariCP pool).
/// </summary>
public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(string connectionString)
    {
        _connectionString = connectionString
            ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new MySqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
