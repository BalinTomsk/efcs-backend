using System.Security.Cryptography;
using System.Text;

namespace NewsIdx.Domain;

/// <summary>Id helpers shared by the indexer, the sync and the vector store.</summary>
public static class NewsIds
{
    /// <summary>
    /// The one spelling of a news id used everywhere in this service: lower-case GUID "D" form.
    /// <c>news.news_id</c> came over from SQL Server, whose GUID strings are upper case, and MySQL's
    /// <c>utf8mb4_unicode_ci</c> compares them case-insensitively — but Qdrant's keyword match does not.
    /// </summary>
    public static string Normalize(string newsId) =>
        Guid.TryParse(newsId, out Guid g) ? g.ToString("D") : newsId.Trim().ToLowerInvariant();

    /// <summary>
    /// Deterministic Qdrant point id for chunk <paramref name="chunk"/> of an article (a name-based,
    /// UUIDv5-style hash of <c>"{newsId}:{chunk}"</c>). Re-indexing an article therefore overwrites its
    /// points in place instead of adding new ones.
    /// </summary>
    public static Guid PointId(string newsId, int chunk)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{Normalize(newsId)}:{chunk}"));
        byte[] bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes, bigEndian: true);
    }
}
