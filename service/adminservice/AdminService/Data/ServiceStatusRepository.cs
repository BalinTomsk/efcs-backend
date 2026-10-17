using System.Data;
using AdminService.Status;
using Microsoft.Data.SqlClient;

namespace AdminService.Data;

/// <summary>Stores service status rows. The only database object adminservice writes through.</summary>
public interface IServiceStatusRepository
{
    /// <summary>Applies one message; <c>false</c> when the row already held a newer one.</summary>
    Task<bool> ReportAsync(ServiceStatusMessage message, CancellationToken cancellationToken);
}

/// <summary>Calls <c>dbo.sp_service_status_report</c> (envfish-db <c>script02_Proc.sql</c>).</summary>
public sealed class SqlServiceStatusRepository : IServiceStatusRepository
{
    private readonly ISqlConnectionFactory _connections;

    public SqlServiceStatusRepository(ISqlConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task<bool> ReportAsync(ServiceStatusMessage message, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "dbo.sp_service_status_report";
        command.CommandTimeout = 15;
        command.Parameters.Add("@serviceName", SqlDbType.VarChar, ServiceStatusMessage.MaxService).Value = message.Service;
        command.Parameters.Add("@machineName", SqlDbType.VarChar, ServiceStatusMessage.MaxMachine).Value = message.Machine;
        command.Parameters.Add("@event", SqlDbType.VarChar, 16).Value = message.Event;
        command.Parameters.Add("@version", SqlDbType.VarChar, ServiceStatusMessage.MaxVersion).Value = (object?)message.Version ?? DBNull.Value;
        command.Parameters.Add("@processStartUtc", SqlDbType.DateTime2).Value = message.ProcessStartUtc;
        command.Parameters.Add("@sentUtc", SqlDbType.DateTime2).Value = message.SentUtc;
        command.Parameters.Add("@lastActionUtc", SqlDbType.DateTime2).Value = (object?)message.LastActionUtc ?? DBNull.Value;
        command.Parameters.Add("@lastAction", SqlDbType.NVarChar, ServiceStatusMessage.MaxAction).Value = (object?)message.LastAction ?? DBNull.Value;

        object? applied = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return applied is int value && value == 1;
    }
}
