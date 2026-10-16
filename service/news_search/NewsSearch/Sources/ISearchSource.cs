namespace NewsSearch.Sources;

/// <summary>
/// One way of finding news. Each source is its own endpoint (<c>GET /api/v1/newssearch/{name}</c>) so the
/// page can ask all of them in parallel and show each result set as it arrives. Today: <c>vector</c> and
/// <c>bm25</c>. Planned: <c>image</c>. The SQL keyword search is not here on purpose: it stays on docapi
/// (jnode), so either node can fail without taking the other's results down.
/// </summary>
public interface ISearchSource
{
    /// <summary>The URL segment, lower case.</summary>
    string Name { get; }

    /// <summary>One line for <c>GET /api/v1/newssearch/sources</c>.</summary>
    string Description { get; }

    /// <summary>Best hits first. Throws <see cref="SourceUnavailableException"/> when the backend cannot answer.</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
}

/// <summary>Validated input, shared by every source.</summary>
public sealed record SearchRequest(string Query, int Limit, string? Country);

/// <summary>
/// One article, in the shape every source returns, so the page can merge them. <c>Score</c> is the source's
/// own relevance (cosine for vector, BM25 for bm25), comparable only within one source.
/// </summary>
public sealed record SearchHit(
    string NewsId, string Title, string? Source, string? Country, string? Date, double Score, string? Snippet, string? LakeId);

/// <summary>The source's backend failed, timed out or has its breaker open (→ HTTP 503).</summary>
public sealed class SourceUnavailableException(string source, string message, Exception? inner = null)
    : Exception($"Source '{source}' is unavailable: {message}", inner)
{
    public string SourceName { get; } = source;
}

/// <summary>Client input error (→ HTTP 400).</summary>
public sealed class InvalidRequestException(string message) : Exception(message);
