using System.Globalization;
using System.Text;
using NewsIdx.Configuration;
using NewsIdx.Domain;
using NewsIdx.Indexing;

namespace NewsIdx.Search;

/// <summary>
/// Turns text into BM25 sparse vectors for Qdrant. The split of the formula:
///
/// <code>
/// score(D, Q) = Σ over terms t in Q:  IDF(t) · tf·(k1+1) / (tf + k1·(1 − b + b·|D|/avgdl))
///               └ Qdrant (modifier=idf) ┘ └──────── stored here, per document term ─────────┘
/// </code>
///
/// The document side is computed once at index time; Qdrant multiplies by IDF at query time from its own
/// live document counts, and the query side is a weight of 1 per distinct term. So adding or removing an
/// article never requires re-encoding the others.
///
/// <para><strong>Analysis:</strong> accents folded, lower-cased, split on anything that is not a letter or
/// digit, English stop words dropped, Porter-stemmed. Each term is hashed (FNV-1a, 32 bit) to its sparse
/// index. Changing any of this changes the indices: bump <see cref="Analyzer"/> and run a full sync.</para>
/// </summary>
public sealed class Bm25Encoder
{
    /// <summary>Stored with every point; a different analyzer needs <c>POST /admin/sync?full=true</c>.</summary>
    public const string Analyzer = "bm25-porter-v1";

    /// <summary>Body text kept in the payload for the snippet.</summary>
    internal const int MaxStoredText = 4000;

    private const int MinTermLength = 2;
    private const int MaxTermLength = 40;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "about", "after", "all", "also", "an", "and", "any", "are", "as", "at", "be", "been", "but", "by",
        "can", "could", "did", "do", "does", "for", "from", "had", "has", "have", "he", "her", "his", "how",
        "if", "in", "into", "is", "it", "its", "more", "no", "not", "of", "on", "or", "our", "out", "she",
        "so", "such", "than", "that", "the", "their", "them", "then", "there", "these", "they", "this", "those",
        "to", "up", "was", "we", "were", "what", "when", "where", "which", "while", "who", "will", "with",
        "would", "you", "your",
    };

    private readonly Bm25Options _options;

    public Bm25Encoder(Bm25Options options)
    {
        _options = options;
    }

    /// <summary>The searchable text of an article: title, then paragraphs and photo captions (cleaned).</summary>
    public static (string Title, string Body) Text(NewsArticle article) =>
        (NewsChunker.Clean(article.Title),
         string.Join("\n", article.Paragraphs.Concat(article.PhotoAlts).Select(NewsChunker.Clean).Where(p => p.Length > 0)));

    /// <summary>
    /// The document vector: one entry per distinct term with its saturated, length-normalized term frequency.
    /// Title terms count <see cref="Bm25Options.TitleWeight"/> times, so a word in the headline outranks the
    /// same word once in the body.
    /// </summary>
    public SparseTerms EncodeDocument(string title, string body)
    {
        var tf = new Dictionary<uint, int>();
        int length = 0;
        int titleWeight = Math.Max(1, _options.TitleWeight);
        foreach (string term in Terms(title))
        {
            uint h = Hash(term);
            tf[h] = tf.GetValueOrDefault(h) + titleWeight;
            length += titleWeight;
        }
        foreach (string term in Terms(body))
        {
            uint h = Hash(term);
            tf[h] = tf.GetValueOrDefault(h) + 1;
            length++;
        }

        double k1 = _options.K1;
        double norm = k1 * (1 - _options.B + _options.B * length / Math.Max(1.0, _options.AvgDocLength));
        uint[] indices = tf.Keys.Order().ToArray();
        float[] values = indices.Select(i => (float)(tf[i] * (k1 + 1) / (tf[i] + norm))).ToArray();
        return new SparseTerms(indices, values);
    }

    /// <summary>The query vector: weight 1 per distinct term. Empty when the query is only stop words.</summary>
    public static SparseTerms EncodeQuery(string query)
    {
        uint[] indices = Terms(query).Select(Hash).Distinct().Order().ToArray();
        return new SparseTerms(indices, indices.Select(_ => 1f).ToArray());
    }

    /// <summary>Analyzed terms, in order.</summary>
    public static IEnumerable<string> Terms(string text)
    {
        foreach ((string word, _) in Words(text))
        {
            if (StopWords.Contains(word))
            {
                continue;
            }
            yield return PorterStemmer.Stem(word);
        }
    }

    /// <summary>
    /// The query's searchable words, once per term, with the term's sparse index: what "Missing: ..." is checked
    /// against. Stop words are left out (they are never indexed). The word is shown as typed, lower-cased.
    /// </summary>
    public static IReadOnlyList<(string Word, uint Term)> QueryWords(string query)
    {
        var seen = new HashSet<uint>();
        var words = new List<(string, uint)>();
        foreach ((string word, _) in Words(query))
        {
            if (StopWords.Contains(word))
            {
                continue;
            }
            uint term = Hash(PorterStemmer.Stem(word));
            if (seen.Add(term))
            {
                words.Add((word, term));
            }
        }
        return words;
    }

    /// <summary>
    /// Where the first word of <paramref name="text"/> matching a query term starts, or -1. Used to centre the
    /// snippet on the match instead of the article's first line.
    /// </summary>
    public static int FirstMatch(string text, string query)
    {
        var wanted = Terms(query).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return -1;
        }
        foreach ((string word, int start) in Words(text))
        {
            if (!StopWords.Contains(word) && wanted.Contains(PorterStemmer.Stem(word)))
            {
                return start;
            }
        }
        return -1;
    }

    /// <summary>Lower-case, accent-free words with their start offset in <paramref name="text"/>.</summary>
    private static IEnumerable<(string Word, int Start)> Words(string text)
    {
        var word = new StringBuilder();
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            char c = i < text.Length ? text[i] : ' ';
            if (char.IsLetterOrDigit(c))
            {
                if (word.Length == 0)
                {
                    start = i;
                }
                AppendFolded(word, c);
                continue;
            }
            if (word.Length >= MinTermLength && word.Length <= MaxTermLength)
            {
                yield return (word.ToString(), start);
            }
            word.Clear();
        }
    }

    /// <summary>"Rivière" → "riviere": decomposes and drops the combining marks, then lower-cases.</summary>
    private static void AppendFolded(StringBuilder word, char c)
    {
        if (c < 0x80)
        {
            word.Append(char.ToLowerInvariant(c));
            return;
        }
        foreach (char d in c.ToString().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(d) != UnicodeCategory.NonSpacingMark)
            {
                word.Append(char.ToLowerInvariant(d));
            }
        }
    }

    /// <summary>FNV-1a over the UTF-8 bytes: stable across processes and .NET versions (unlike GetHashCode).</summary>
    internal static uint Hash(string term)
    {
        uint hash = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(term))
        {
            hash = (hash ^ b) * 16777619;
        }
        return hash;
    }
}

/// <summary>A sparse vector: sorted distinct indices and their weights.</summary>
public sealed record SparseTerms(uint[] Indices, float[] Values)
{
    public bool IsEmpty => Indices.Length == 0;
}
