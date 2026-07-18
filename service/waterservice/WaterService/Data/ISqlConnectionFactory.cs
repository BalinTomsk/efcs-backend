using Microsoft.Data.SqlClient;

namespace WaterService.Data;

/// <summary>
/// Opens ready-to-use connections to the FishFind SQL Server database.
/// </summary>
public interface ISqlConnectionFactory
{
    /// <summary>Opens a new connection asynchronously.</summary>
    Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens a new connection synchronously.</summary>
    SqlConnection Open();
}
