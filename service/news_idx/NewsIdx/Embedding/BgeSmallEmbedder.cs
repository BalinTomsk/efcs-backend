using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using NewsIdx.Configuration;

namespace NewsIdx.Embedding;

/// <summary>
/// In-process BAAI/bge-small-en-v1.5 embeddings: the model's ONNX export run by ONNX Runtime on the CPU,
/// tokenized with its own WordPiece vocabulary. No external embedding service and no network call.
///
/// <para><strong>Pooling follows the model card:</strong> the <c>[CLS]</c> token's last hidden state,
/// L2-normalized — not mean pooling. Queries get BGE's retrieval instruction prefix; passages do not.</para>
///
/// <para>Model files are not in git (about 130 MB). <c>scripts/fetch-model.ps1</c> downloads them into
/// <c>models/</c>, and the Docker build copies that folder into the image.</para>
/// </summary>
public sealed class BgeSmallEmbedder : ITextEmbedder, IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly EmbeddingOptions _options;
    private readonly HashSet<string> _inputs;
    private readonly string _output;

    public BgeSmallEmbedder(IOptions<NewsIdxOptions> options, ILogger<BgeSmallEmbedder> logger)
    {
        _options = options.Value.Embedding;

        string modelPath = Path.GetFullPath(_options.ModelPath);
        string vocabPath = Path.GetFullPath(_options.VocabPath);
        if (!File.Exists(modelPath) || !File.Exists(vocabPath))
        {
            throw new InvalidOperationException(
                $"Embedding model not found (model={modelPath}, vocab={vocabPath}). "
                + "Run scripts/fetch-model.ps1, or set NewsIdx__Embedding__ModelPath / __VocabPath.");
        }

        var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            IntraOpNumThreads = Math.Max(1, _options.Threads),
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(modelPath, sessionOptions);
        _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions { LowerCaseBeforeTokenization = true });

        _inputs = _session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
        _output = _session.OutputMetadata.Keys.Contains("last_hidden_state")
            ? "last_hidden_state"
            : _session.OutputMetadata.Keys.First();

        int[] dims = _session.OutputMetadata[_output].Dimensions;
        Dimension = dims[^1] > 0 ? dims[^1] : 384;

        logger.LogInformation("Embedding model loaded. model={Model} dimension={Dimension} inputs={Inputs} output={Output}",
            ModelName, Dimension, string.Join(",", _inputs), _output);
    }

    public int Dimension { get; }

    public string ModelName => "bge-small-en-v1.5";

    public Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken)
    {
        var vectors = new List<float[]>(passages.Count);
        int batch = Math.Max(1, _options.BatchSize);
        for (int i = 0; i < passages.Count; i += batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            vectors.AddRange(Run(passages.Skip(i).Take(batch).ToList()));
        }
        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken) =>
        Task.FromResult(Run([_options.QueryInstruction + query])[0]);

    /// <summary>One ONNX call for a batch: tokenize, pad to the longest, run, take [CLS], normalize.</summary>
    private float[][] Run(IReadOnlyList<string> texts)
    {
        int max = Math.Clamp(_options.MaxTokens, 8, 512);
        List<IReadOnlyList<int>> ids = texts
            .Select(t => _tokenizer.EncodeToIds(t, max, addSpecialTokens: true, out _, out _))
            .ToList();
        int length = ids.Max(x => x.Count);

        var inputIds = new DenseTensor<long>([texts.Count, length]);
        var attention = new DenseTensor<long>([texts.Count, length]);
        var tokenTypes = new DenseTensor<long>([texts.Count, length]); // single segment: all zeros
        for (int b = 0; b < ids.Count; b++)
        {
            for (int t = 0; t < ids[b].Count; t++)
            {
                inputIds[b, t] = ids[b][t];
                attention[b, t] = 1;
            }
        }

        var feeds = new List<NamedOnnxValue>(3);
        if (_inputs.Contains("input_ids")) feeds.Add(NamedOnnxValue.CreateFromTensor("input_ids", inputIds));
        if (_inputs.Contains("attention_mask")) feeds.Add(NamedOnnxValue.CreateFromTensor("attention_mask", attention));
        if (_inputs.Contains("token_type_ids")) feeds.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypes));

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(feeds, [_output]);
        Tensor<float> hidden = results[0].AsTensor<float>();

        var vectors = new float[texts.Count][];
        for (int b = 0; b < texts.Count; b++)
        {
            var v = new float[Dimension];
            for (int d = 0; d < Dimension; d++)
            {
                // [batch, seq, dim] → the [CLS] token (position 0); a pooled [batch, dim] output is used as is.
                v[d] = hidden.Rank == 3 ? hidden[b, 0, d] : hidden[b, d];
            }
            vectors[b] = Normalize(v);
        }
        return vectors;
    }

    internal static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (float x in v)
        {
            sum += x * x;
        }

        float norm = (float)Math.Sqrt(sum);
        if (norm > 0)
        {
            for (int i = 0; i < v.Length; i++)
            {
                v[i] /= norm;
            }
        }
        return v;
    }

    public void Dispose() => _session.Dispose();
}
