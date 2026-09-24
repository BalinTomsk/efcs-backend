namespace WaterApi.Domain;

/// <summary>
/// An immutable, fully-loaded copy of one country's map stations, as held by the cache. Readers never see
/// a half-built snapshot: a refresh builds a new instance and swaps the reference.
/// </summary>
/// <param name="Country">Two-letter country code this snapshot covers.</param>
/// <param name="Stations">Every station for the country, ordered by <see cref="MapStation.Sid"/>.</param>
/// <param name="LoadedUtc">When the snapshot was read from the database.</param>
public sealed record StationMapSnapshot(
    string Country,
    IReadOnlyList<MapStation> Stations,
    DateTime LoadedUtc)
{
    /// <summary>The same stations keyed by sid, for single-station lookups.</summary>
    public IReadOnlyDictionary<int, MapStation> BySid { get; } = Stations.ToDictionary(s => s.Sid);

    /// <summary>
    /// Entity tag for HTTP revalidation of one view (<paramref name="state"/> filter, or all) of this
    /// snapshot. Changes on every refresh, so the frontend can send <c>If-None-Match</c> and get a 304
    /// instead of re-downloading tens of thousands of pins.
    /// </summary>
    public string ETagFor(string? state) =>
        $"\"{Country}-{(string.IsNullOrEmpty(state) ? "ALL" : state)}-{LoadedUtc.Ticks:x}-{Stations.Count}\"";
}
