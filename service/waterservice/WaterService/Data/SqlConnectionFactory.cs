using Microsoft.Data.SqlClient;

namespace WaterService.Data;

/// <summary>
/// Default <see cref="ISqlConnectionFactory"/> that hands out fresh <see cref="SqlConnection"/>s from a
/// single connection string. SqlClient pools the underlying physical connections, so this is the
/// pooling analogue of the Java HikariCP datasource.
/// </summary>
public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(string connectionString)
    {
        _connectionString = connectionString
            ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(_connectionString);
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

    public SqlConnection Open()
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
