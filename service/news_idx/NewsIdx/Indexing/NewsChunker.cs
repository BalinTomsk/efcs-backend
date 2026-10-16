using System.Net;
using System.Text.RegularExpressions;
using NewsIdx.Configuration;
using NewsIdx.Domain;

namespace NewsIdx.Indexing;

/// <summary>
/// Splits an article into overlapping passages small enough for BERT's 512-token window. Each passage
/// starts with the article title, so a chunk from the middle of a long article still says what it is about.
///
/// <para>The text indexed is title, the three paragraphs and the photo captions (alt text), the same fields
/// docapi's keyword search looks at. Markup is stripped and entities decoded; the editor stores plain text,
/// but imported articles may carry tags.</para>
/// </summary>
public static partial class NewsChunker
{
    public static IReadOnlyList<string> Chunk(NewsArticle article, ChunkingOptions options)
    {
        string title = Clean(article.Title);
        string body = string.Join("\n", article.Paragraphs.Concat(article.PhotoAlts).Select(Clean).Where(p => p.Length > 0));

        string[] words = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return [title];
        }

        int size = Math.Max(20, options.ChunkWords);
        int overlap = Math.Clamp(options.OverlapWords, 0, size / 2);
        int step = size - overlap;

        var chunks = new List<string>();
        for (int start = 0; start < words.Length; start += step)
        {
            int count = Math.Min(size, words.Length - start);
            chunks.Add(title + "\n" + string.Join(' ', words, start, count));
            if (start + count >= words.Length)
            {
                break;
            }
        }
        return chunks;
    }

    /// <summary>Plain text: tags removed, entities decoded, whitespace collapsed.</summary>
    internal static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string noTags = TagPattern().Replace(text, " ");
        return SpacePattern().Replace(WebUtility.HtmlDecode(noTags), " ").Trim();
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacePattern();
}
