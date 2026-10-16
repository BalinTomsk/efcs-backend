using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewsFind.Configuration;
using NewsFind.Embedding;
using TUnit.Core;

namespace NewsFind.Tests;

/// <summary>
/// The real bge-small-en-v1.5 model. Runs only when <c>models/bge-small-en-v1.5</c> exists (fetched by
/// <c>scripts/fetch-model.ps1</c>); without it each test returns at once, so CI without the 130 MB file passes.
/// </summary>
public class ModelTests
{
    private static readonly Lazy<BgeSmallEmbedder?> Embedder = new(Load);

    [Test]
    public async Task Model_Produces384DimUnitVectors()
    {
        if (Embedder.Value is not { } e) return;

        IReadOnlyList<float[]> v = await e.EmbedPassagesAsync(["Walleye season opens in May.", "x"], CancellationToken.None);

        await Assert.That(e.Dimension).IsEqualTo(384);
        await Assert.That(v.Count).IsEqualTo(2);
        await Assert.That(Math.Abs(Dot(v[0], v[0]) - 1f)).IsLessThan(1e-4f);
    }

    [Test]
    public async Task Model_BatchAndSingle_AgreeDespitePadding()
    {
        if (Embedder.Value is not { } e) return;

        float[] alone = (await e.EmbedPassagesAsync(["Lake trout stocking in Ontario."], CancellationToken.None))[0];
        float[] batched = (await e.EmbedPassagesAsync(
            ["Lake trout stocking in Ontario.", "A much longer second passage so the first one is padded in the batch tensor."],
            CancellationToken.None))[0];

        await Assert.That(Dot(alone, batched)).IsGreaterThan(0.999f);
    }

    [Test]
    public async Task Model_QueryFindsTheArticleThatMeansTheSame_WithoutSharedWords()
    {
        if (Embedder.Value is not { } e) return;

        IReadOnlyList<float[]> passages = await e.EmbedPassagesAsync(
        [
            "Anglers may now keep four pickerel per day on northern lakes, down from six.",
            "The city council approved a new budget for road repairs downtown.",
            "Hockey playoffs: the local team won in overtime.",
        ], CancellationToken.None);
        float[] query = await e.EmbedQueryAsync("walleye catch limit reduced", CancellationToken.None);

        float[] scores = passages.Select(p => Dot(query, p)).ToArray();        await Assert.That(scores[0]).IsGreaterThan(scores[1]);
        await Assert.That(scores[0]).IsGreaterThan(scores[2]);
    }

    private static BgeSmallEmbedder? Load()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string folder = Path.Combine(dir.FullName, "models", "bge-small-en-v1.5");
            if (File.Exists(Path.Combine(folder, "model.onnx")))
            {
                var options = new NewsFindOptions();
                options.Embedding.ModelPath = Path.Combine(folder, "model.onnx");
                options.Embedding.VocabPath = Path.Combine(folder, "vocab.txt");
                return new BgeSmallEmbedder(Options.Create(options), NullLogger<BgeSmallEmbedder>.Instance);
            }
        }
        return null;
    }

    private static float Dot(float[] a, float[] b) => a.Zip(b, (x, y) => x * y).Sum();
}
