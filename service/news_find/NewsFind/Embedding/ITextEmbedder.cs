namespace NewsFind.Embedding;

/// <summary>Turns text into unit-length vectors (cosine similarity = dot product).</summary>
public interface ITextEmbedder
{
    /// <summary>Vector size — 384 for bge-small-en-v1.5. The Qdrant collection is created with it.</summary>
    int Dimension { get; }

    /// <summary>Model name stored on every point, so a model change is visible in the index.</summary>
    string ModelName { get; }

    /// <summary>Embeds article passages (no instruction prefix).</summary>
    Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken);

    /// <summary>Embeds a search query (with BGE's retrieval instruction).</summary>
    Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken);
}
