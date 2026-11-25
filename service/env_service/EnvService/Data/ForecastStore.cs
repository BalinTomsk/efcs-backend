using System.Data;
using EnvService.Configuration;
using Microsoft.Data.SqlClient;
using Polly;
using Polly.Registry;

namespace EnvService.Data;

/// <summary>
/// The SQL Server halves of the forecast documents (plan step 6b): <c>env.fn_forecast_stations_json</c>,
/// <c>env.fn_fish_reasoning_json</c>, <c>env.fn_forecast_plot_meta_json</c> (<c>envfish-db/mssql</c>). Each returns
/// the database's JSON document, or null (unknown fish, or a station / fish pair that does not exist).
/// <see cref="SqlForecastStore"/> is the real one; tests use fakes.
/// </summary>
public interface IForecastStore
{
    Task<string?> StationsJsonAsync(Guid fishId, double lat, double lon, int radiusKm, string? country, int limit, CancellationToken ct);

    Task<string?> ReasoningJsonAsync(int sid, Guid fishId, DateTime atUtc, CancellationToken ct);

    Task<string?> PlotMetaJsonAsync(int sid, Guid fishId, CancellationToken ct);
}

/// <summary>
/// <see cref="IForecastStore"/> on SQL Server, through the <c>sql-read</c> pipeline (a request never waits for the
/// jobs' retries). One scalar function call per method.
/// </summary>
public sealed class SqlForecastStore(string connectionString, ResiliencePipelineProvider<string> pipelines) : IForecastStore
{
    private readonly ResiliencePipeline _sql = pipelines.GetPipeline(ResiliencePipelines.SqlRead);

    public Task<string?> StationsJsonAsync(
        Guid fishId, double lat, double lon, int radiusKm, string? country, int limit, CancellationToken ct) =>
        ScalarAsync("SELECT env.fn_forecast_stations_json(@fish, @lat, @lon, @radius, @country, @limit)", c =>
        {
            c.Parameters.Add("@fish", SqlDbType.UniqueIdentifier).Value = fishId;
            c.Parameters.Add("@lat", SqlDbType.Float).Value = lat;
            c.Parameters.Add("@lon", SqlDbType.Float).Value = lon;
            c.Parameters.Add("@radius", SqlDbType.Float).Value = (double)radiusKm;
            c.Parameters.Add("@country", SqlDbType.Char, 2).Value = (object?)country ?? DBNull.Value;
            c.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        }, ct);

    public Task<string?> ReasoningJsonAsync(int sid, Guid fishId, DateTime atUtc, CancellationToken ct) =>
        ScalarAsync("SELECT env.fn_fish_reasoning_json(@sid, @fish, @at)", c =>
        {
            c.Parameters.Add("@sid", SqlDbType.Int).Value = sid;
            c.Parameters.Add("@fish", SqlDbType.UniqueIdentifier).Value = fishId;
            c.Parameters.Add("@at", SqlDbType.DateTime2).Value = atUtc;
        }, ct);

    public Task<string?> PlotMetaJsonAsync(int sid, Guid fishId, CancellationToken ct) =>
        ScalarAsync("SELECT env.fn_forecast_plot_meta_json(@sid, @fish)", c =>
        {
            c.Parameters.Add("@sid", SqlDbType.Int).Value = sid;
            c.Parameters.Add("@fish", SqlDbType.UniqueIdentifier).Value = fishId;
        }, ct);

    private Task<string?> ScalarAsync(string sql, Action<SqlCommand> configure, CancellationToken ct) =>
        _sql.ExecuteAsync(async token =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
            configure(command);
            object? result = await command.ExecuteScalarAsync(token);
            return result is null or DBNull ? null : (string)result;
        }, ct).AsTask();
}
