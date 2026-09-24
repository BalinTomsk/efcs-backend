namespace WaterApi.Domain;

/// <summary>
/// One water-station pin on the frontend map — a row of <c>dbo.vMapView</c>.
/// </summary>
/// <param name="Sid">Internal numeric station id (<c>WaterStation.sid</c>).</param>
/// <param name="Key">Base-36 form of <see cref="Sid"/>, the id the map's links carry (same encoding as
/// <c>Editor/ViewMap.aspx.cs</c> <c>ToBase36</c>).</param>
/// <param name="Lat">Latitude, decimal degrees.</param>
/// <param name="Lon">Longitude, decimal degrees.</param>
/// <param name="Country">Two-letter country code, trimmed (<c>WaterStation.country</c> is <c>char(3)</c>).</param>
/// <param name="State">Two-letter province/state code, or empty.</param>
/// <param name="Stamp">Time of the station's latest reading (<c>CurrentWaterState.stamp</c>, UTC).</param>
public sealed record MapStation(
    int Sid,
    string Key,
    double Lat,
    double Lon,
    string Country,
    string State,
    DateTime Stamp)
{
    private const string Base36Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>Encodes a station id in base 36, upper case — identical to the legacy page's encoder.</summary>
    public static string ToBase36(ulong value)
    {
        Span<char> buffer = stackalloc char[13];
        int pos = buffer.Length;
        do
        {
            buffer[--pos] = Base36Digits[(int)(value % 36)];
            value /= 36;
        } while (value != 0);
        return new string(buffer[pos..]);
    }
}
