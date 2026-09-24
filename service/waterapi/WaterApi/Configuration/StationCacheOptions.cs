namespace WaterApi.Configuration;

/// <summary>
/// Station map cache settings, bound from the <c>WaterApi:StationCache</c> section (override with
/// <c>WaterApi__StationCache__RefreshMinutes=…</c> style environment variables).
/// </summary>
public sealed class StationCacheOptions
{
    public const string SectionName = "WaterApi:StationCache";

    /// <summary>
    /// Countries the cache serves; any other value is a 400. Empty means <see cref="DefaultCountries"/>.
    ///
    /// <para>Deliberately defaults to empty: the configuration binder <em>appends</em> to an array that
    /// already has elements, so a <c>["CA","US"]</c> default plus the same list in appsettings.json bound
    /// to <c>CA,US,CA,US</c> and every refresh loaded each country twice.
    /// <see cref="StationMapCache"/> also upper-cases and de-duplicates the list.</para>
    /// </summary>
    public string[] Countries { get; set; } = [];

    /// <summary>Used when <see cref="Countries"/> is empty.</summary>
    public static readonly string[] DefaultCountries = ["CA", "US"];

    /// <summary>
    /// How often the background refresher reloads every country from the database. <c>vMapView</c> only
    /// changes as readings arrive (hourly, from water-station-pusher), so minutes-old data is fine.
    /// </summary>
    public int RefreshMinutes { get; set; } = 15;

    /// <summary>
    /// A snapshot older than this is still served (better a slightly old map than none) but the response
    /// is flagged <c>meta.stale = true</c> and a warning is logged on each failed refresh.
    /// </summary>
    public int MaxStaleMinutes { get; set; } = 360;

    /// <summary>Load every country at startup so the first frontend request never waits on the view.</summary>
    public bool WarmOnStartup { get; set; } = true;

    /// <summary><c>Cache-Control: max-age</c> sent to browsers / the gateway for map responses.</summary>
    public int ClientMaxAgeSeconds { get; set; } = 300;
}
