namespace NewsIdx.Configuration;

/// <summary>
/// Non-secret tuning, bound from the <c>NewsIdx</c> section (override with <c>NewsIdx__Search__MaxLimit=…</c>
/// style environment variables). Connection details and credentials are flat keys instead
/// (<c>MYSQL_NEWS_URL</c>, <c>QDRANT_URL</c>, <c>RABBITMQ_*</c>), like <c>DB_URL</c> in the siblings.
/// </summary>
public sealed class NewsIdxOptions
{
    public const string SectionName = "NewsIdx";

    public QdrantOptions Qdrant { get; set; } = new();
    public EmbeddingOptions Embedding { get; set; } = new();
    public ChunkingOptions Chunking { get; set; } = new();
    public SearchOptions Search { get; set; } = new();
    public Bm25Options Bm25 { get; set; } = new();
    public EventOptions Events { get; set; } = new();
    public SyncOptions Sync { get; set; } = new();
}

public sealed class QdrantOptions
{
    /// <summary>Collection holding one point per article chunk.</summary>
    public string Collection { get; set; } = "news_articles";

    /// <summary>gRPC call deadline.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class EmbeddingOptions
{
    /// <summary>ONNX export of BAAI/bge-small-en-v1.5 (<c>onnx/model.onnx</c> in the model repository).</summary>
    public string ModelPath { get; set; } = "models/bge-small-en-v1.5/model.onnx";

    /// <summary>The model's WordPiece vocabulary (<c>vocab.txt</c>).</summary>
    public string VocabPath { get; set; } = "models/bge-small-en-v1.5/vocab.txt";

    /// <summary>BERT's hard limit; longer input is truncated (chunks are sized well under it).</summary>
    public int MaxTokens { get; set; } = 512;

    /// <summary>
    /// ONNX intra-op threads. 1 by default: the target droplets have one or two shared vCPUs and run other
    /// services, and indexing is background work that can afford to be slow.
    /// </summary>
    public int Threads { get; set; } = 1;

    /// <summary>Chunks embedded per ONNX call.</summary>
    public int BatchSize { get; set; } = 8;

    /// <summary>
    /// Instruction BGE v1.5 prepends to short <em>queries</em> (never to passages) for retrieval.
    /// </summary>
    public string QueryInstruction { get; set; } = "Represent this sentence for searching relevant passages: ";
}

public sealed class ChunkingOptions
{
    /// <summary>
    /// Words per chunk. About 1.3 BERT tokens per English word, so 220 words plus the title stays under
    /// 512 tokens with room to spare.
    /// </summary>
    public int ChunkWords { get; set; } = 220;

    /// <summary>Words repeated at the start of the next chunk, so a sentence cut at a boundary is still found.</summary>
    public int OverlapWords { get; set; } = 40;
}

public sealed class SearchOptions
{
    public int DefaultLimit { get; set; } = 10;
    public int MaxLimit { get; set; } = 50;
    public int MaxQueryLength { get; set; } = 256;

    /// <summary>Cosine similarity below which a hit is dropped. 0 keeps everything.</summary>
    public float MinScore { get; set; } = 0.0f;
}

/// <summary>
/// Keyword (BM25) search. Changing <c>K1</c>, <c>B</c>, <c>AvgDocLength</c> or <c>TitleWeight</c> changes the
/// stored document weights: run <c>POST /admin/sync?full=true</c> afterwards.
/// </summary>
public sealed class Bm25Options
{
    /// <summary>Collection holding one point per article with its sparse term vector.</summary>
    public string Collection { get; set; } = "news_articles_bm25";

    /// <summary>Term-frequency saturation: how quickly repeating a word stops adding to the score.</summary>
    public double K1 { get; set; } = 1.2;

    /// <summary>Length normalization: 0 ignores article length, 1 normalizes fully.</summary>
    public double B { get; set; } = 0.75;

    /// <summary>
    /// Average article length in analyzed terms (title counted <see cref="TitleWeight"/> times). Fixed rather
    /// than measured, so one article's edit never shifts every other article's weights.
    /// </summary>
    public double AvgDocLength { get; set; } = 256;

    /// <summary>How many times a title word counts.</summary>
    public int TitleWeight { get; set; } = 2;
}

public sealed class EventOptions
{
    /// <summary>Queue the frontend publishes news events to. Distinct from cproxy's account queue.</summary>
    public string Queue { get; set; } = "fishfind.newsidx.events";

    public int PollMilliseconds { get; set; } = 2000;
    public int BatchSize { get; set; } = 20;

    /// <summary>A message that fails this many times is dropped (logged); the periodic sync repairs it.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Upper bound of the poll back-off while MySQL or Qdrant is failing.</summary>
    public int MaxBackoffSeconds { get; set; } = 60;

    public int HttpTimeoutSeconds { get; set; } = 10;
}

public sealed class SyncOptions
{
    /// <summary>
    /// Startup behaviour: <c>IfEmpty</c> (default) runs a full index when the collection has no points,
    /// <c>Always</c> runs an incremental sync, <c>Never</c> does nothing.
    /// </summary>
    public string OnStartup { get; set; } = "IfEmpty";

    /// <summary>
    /// Minutes between incremental syncs against MySQL (0 disables). It repairs anything the events missed —
    /// a lost message, an edit made from the database control panel — and costs one narrow query plus a
    /// scroll of the collection when nothing changed.
    /// </summary>
    public int IntervalMinutes { get; set; } = 60;
}
