using Microsoft.AspNetCore.Diagnostics;
using NewsSearch.Sources;

namespace NewsSearch.Web;

/// <summary>
/// Exceptions → the <see cref="ApiResponse{T}"/> error envelope: bad input 400, unknown source 404, a source
/// whose backend is down 503 with <c>Retry-After</c> (the page then just shows the other sources), anything
/// else a logged, generic 500.
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
            UnknownSourceException e => (StatusCodes.Status404NotFound, "not_found", e.Message),
            SourceUnavailableException e => (StatusCodes.Status503ServiceUnavailable, "unavailable",
                $"Search source '{e.SourceName}' is temporarily unavailable"),
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
