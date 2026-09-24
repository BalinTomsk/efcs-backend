using Microsoft.AspNetCore.Diagnostics;
using WaterApi.Services;

namespace WaterApi.Web;

/// <summary>
/// Translates exceptions into the <see cref="ApiResponse{T}"/> error envelope with an appropriate HTTP
/// status (docapi's <c>ApiExceptionHandler</c> counterpart). Client mistakes are 4xx with a descriptive
/// message; a map that has never loaded is a 503; anything else is logged and returned as a generic 500
/// so internal details (SQL, stack traces) never leak to callers.
/// </summary>
public sealed class ApiExceptionHandler : IExceptionHandler
{
    private readonly ILogger<ApiExceptionHandler> _logger;

    public ApiExceptionHandler(ILogger<ApiExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        (int status, string code, string message) = exception switch
        {
            InvalidRequestException e => (StatusCodes.Status400BadRequest, "invalid_request", e.Message),
            StationNotFoundException e => (StatusCodes.Status404NotFound, "not_found", e.Message),
            StationCacheUnavailableException e => (StatusCodes.Status503ServiceUnavailable, "unavailable", e.Message),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred"),
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Unhandled error serving {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        if (status == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.RetryAfter = "30";
        }

        await httpContext.Response.WriteAsJsonAsync(ApiResponse.Fail(code, message), cancellationToken).ConfigureAwait(false);
        return true;
    }
}
