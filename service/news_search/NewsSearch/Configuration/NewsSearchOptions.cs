namespace NewsSearch.Configuration;

/// <summary>
/// Settings bound from the <c>NewsSearch</c> section (override with <c>NewsSearch__Search__MaxLimit=…</c>).
/// Upstream addresses are flat keys (<c>NEWSIDX_URL</c>), like <c>DB_URL</c> in the siblings.
/// </summary>
public sealed class NewsSearchOptions
{
    public const string SectionName = "NewsSearch";

    public SearchOptions Search { get; set; } = new();
    public VectorSourceOptions Vector { get; set; } = new();
}

public sealed class SearchOptions
{
    public int DefaultLimit { get; set; } = 10;
    public int MaxLimit { get; set; } = 50;
    public int MaxQueryLength { get; set; } = 256;
}

public sealed class VectorSourceOptions
{
    /// <summary>
    /// Whole-request budget for one news_idx call. Short on purpose: the page shows this source's results
    /// when they arrive, so a slow answer is better dropped than waited for.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 5;
}
