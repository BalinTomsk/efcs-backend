namespace WaterService.Processing;

/// <summary>
/// Result of processing a single station.
/// </summary>
public enum ProcessingOutcome
{
    /// <summary>Water data was fetched and persisted.</summary>
    Processed,

    /// <summary>Station has no published source feed, such as HTTP 404.</summary>
    Skipped,

    /// <summary>Processing failed because the upstream provider returned HTTP 503.</summary>
    FailedHttp503,

    /// <summary>Processing failed for any other handled reason.</summary>
    Failed,
}
