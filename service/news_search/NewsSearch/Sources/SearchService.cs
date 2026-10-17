using System.Diagnostics;
using Microsoft.Extensions.Options;
using NewsSearch.Configuration;
using NewsSearch.Status;
using Prometheus;

namespace NewsSearch.Sources;

/// <summary>
/// Validates a request once, then hands it to the named source. Every source sees the same normalized input,
/// so the page can merge their answers.
/// </summary>
public sealed class SearchService
{
    private static readonly Counter Requests = Metrics.CreateCounter(
        "newssearch_requests_total", "Search requests by source and outcome.", "source", "outcome");
    private static readonly Histogram Latency = Metrics.CreateHistogram(
        "newssearch_source_seconds", "Time spent in a source, by source.", "source");

    private readonly IReadOnlyDictionary<string, ISearchSource> _sources;
    private readonly SearchOptions _options;
    private readonly ServiceActivity? _activity;

    public SearchService(IEnumerable<ISearchSource> sources, IOptions<NewsSearchOptions> options, ServiceActivity? activity = null)
    {
        _activity = activity;
        _sources = sources.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        _options = options.Value.Search;
    }

    public IEnumerable<ISearchSource> Sources => _sources.Values;

    public async Task<SourceResult> SearchAsync(string source, string? query, int? limit, string? country, CancellationToken ct)
    {
        if (!_sources.TryGetValue(source, out ISearchSource? s))
        {
            throw new UnknownSourceException(source);
        }

        SearchRequest request = Validate(query, limit, country);
        _activity?.Record("search " + s.Name);  // the source only: a query is the visitor's, not the admin page's
        var watch = Stopwatch.StartNew();
        try
        {
            IReadOnlyList<SearchHit> hits;
            using (Latency.WithLabels(s.Name).NewTimer())
            {
                hits = await s.SearchAsync(request, ct).ConfigureAwait(false);
            }
            Requests.WithLabels(s.Name, "ok").Inc();
            return new SourceResult(s.Name, request.Query, request.Country, hits.Count, watch.ElapsedMilliseconds, hits);
        }
        catch (SourceUnavailableException)
        {
            Requests.WithLabels(s.Name, "unavailable").Inc();
            throw;
        }
    }

    internal SearchRequest Validate(string? query, int? limit, string? country)
    {
        string q = (query ?? string.Empty).Trim();
        if (q.Length == 0)
        {
            throw new InvalidRequestException("q is required");
        }
        if (q.Length > _options.MaxQueryLength)
        {
            throw new InvalidRequestException($"q must be at most {_options.MaxQueryLength} characters");
        }

        int take = limit ?? _options.DefaultLimit;
        if (take < 1 || take > _options.MaxLimit)
        {
            throw new InvalidRequestException($"limit must be between 1 and {_options.MaxLimit}");
        }

        string? c = null;
        if (!string.IsNullOrWhiteSpace(country))
        {
            c = country.Trim().ToUpperInvariant();
            if (c.Length != 2 || !char.IsAsciiLetterUpper(c[0]) || !char.IsAsciiLetterUpper(c[1]))
            {
                throw new InvalidRequestException($"country must be a two-letter code; got '{country.Trim()}'");
            }
        }

        return new SearchRequest(q, take, c);
    }
}

/// <summary><c>data</c> of <c>GET /api/v1/newssearch/{source}</c>.</summary>
public sealed record SourceResult(
    string Source, string Query, string? Country, int Count, long TookMs, IReadOnlyList<SearchHit> Items);

/// <summary>No source by that name (→ HTTP 404).</summary>
public sealed class UnknownSourceException(string source) : Exception($"No search source '{source}'");
