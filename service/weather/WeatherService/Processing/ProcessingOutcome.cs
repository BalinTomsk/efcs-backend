namespace WeatherService.Processing;

/// <summary>
/// Result of processing a single station, used by the worker to decide — at the end of a cycle —
/// whether the run was healthy enough to trigger post-processing.
/// </summary>
public enum ProcessingOutcome
{
    /// <summary>Forecast/observation was fetched and persisted.</summary>
    Processed,

    /// <summary>Station has no published feed with this provider (HTTP 404); a normal, expected skip.</summary>
    Skipped,

    /// <summary>Processing failed because the upstream provider returned HTTP 503.</summary>
    FailedHttp503,

    /// <summary>Processing failed (network error, rate limit, SQL failure, open circuit, …).</summary>
    Failed,
}
