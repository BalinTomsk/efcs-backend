namespace WaterApi.Web;

/// <summary>
/// Standard response envelope used by every API endpoint: <c>{ data, error, meta }</c> — the same
/// platform-wide convention as docapi's <c>ApiResponse</c>. Exactly one of <c>data</c> / <c>error</c> is
/// populated; <c>meta</c> always carries a response timestamp.
/// </summary>
public sealed record ApiResponse<T>(T? Data, ApiError? Error, IDictionary<string, object?> Meta)
{
    public static ApiResponse<T> Ok(T data, IDictionary<string, object?>? extraMeta = null) =>
        new(data, null, NewMeta(extraMeta));

    internal static IDictionary<string, object?> NewMeta(IDictionary<string, object?>? extra = null)
    {
        var meta = new Dictionary<string, object?> { ["timestamp"] = DateTimeOffset.UtcNow };
        if (extra is not null)
        {
            foreach ((string key, object? value) in extra)
            {
                meta[key] = value;
            }
        }
        return meta;
    }
}

/// <summary>Machine-readable error code plus a human-readable message.</summary>
public sealed record ApiError(string Code, string Message);

/// <summary>Error-envelope factory (<c>data</c> always null).</summary>
public static class ApiResponse
{
    public static ApiResponse<object> Fail(string code, string message) =>
        new(null, new ApiError(code, message), ApiResponse<object>.NewMeta());
}
