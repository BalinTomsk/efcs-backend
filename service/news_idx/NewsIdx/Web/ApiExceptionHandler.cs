using Microsoft.AspNetCore.Diagnostics;
using NewsIdx.Search;
using Polly.CircuitBreaker;

namespace NewsIdx.Web;

/// <summary>
/// Exceptions → the <see cref="ApiResponse{T}"/> error envelope. Bad input is a 400; an open breaker (Qdrant
/// down) is a 503 with <c>Retry-After</c>; anything else is logged and returned as a generic 500, so
/// internal details never reach callers.
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
            BrokenCircuitException => (StatusCodes.Status503ServiceUnavailable, "unavailable", "Search is temporarily unavailable"),
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
