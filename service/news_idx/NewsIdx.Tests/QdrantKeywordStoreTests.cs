using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewsIdx.Configuration;
using NewsIdx.Domain;
using NewsIdx.Search;
using Qdrant.Client;
using TUnit.Core;

namespace NewsIdx.Tests;

/// <summary>
/// <see cref="QdrantKeywordStore"/> against a real Qdrant: the sparse vector, the server-side IDF and the
/// filters cannot be checked with fakes. Runs only when <c>QDRANT_TEST_URL</c> (gRPC, e.g.
/// <c>http://localhost:6334</c>) is set; uses a throw-away collection and deletes it.
/// </summary>
public class QdrantKeywordStoreTests
{
    [Test]
    public async Task RealQdrant_IndexesSearchesAndDeletes()
    {
        string? url = Environment.GetEnvironmentVariable("QDRANT_TEST_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            Skip.Test("QDRANT_TEST_URL not set");
            return;
        }

        string collection = "test_bm25_" + Guid.NewGuid().ToString("N")[..8];
        var client = new QdrantClient(new Uri(url));
        NewsIdxOptions options = TestSupport.Options(o => o.Bm25.Collection = collection);
        var store = new QdrantKeywordStore(client, TestSupport.Pipelines(), Options.Create(options), NullLogger<QdrantKeywordStore>.Instance);
        var encoder = new Bm25Encoder(options.Bm25);
        try
        {
            await store.EnsureCollectionAsync(CancellationToken.None);
            await store.EnsureCollectionAsync(CancellationToken.None); // second call validates the existing one

            NewsArticle[] articles =
            [
                FakeArticleSource.Article(Guid.NewGuid().ToString("D"), "Walleye limits", "new walleye limits on the lake"),
                FakeArticleSource.Article(Guid.NewGuid().ToString("D"), "Keta salmon run", "keta salmon reached the lake"),
                FakeArticleSource.Article(Guid.NewGuid().ToString("D"), "Bass derby", "bass derby on the lake", country: "US"),
            ];
            foreach (NewsArticle a in articles)
            {
                (string title, string body) = Bm25Encoder.Text(a);
                await store.ReplaceArticleAsync(a, body, encoder.EncodeDocument(title, body), CancellationToken.None);
            }

            IReadOnlyList<KeywordHit> keta = await store.SearchAsync(Bm25Encoder.EncodeQuery("lake keta"), 10, null, CancellationToken.None);
            IReadOnlyList<KeywordHit> us = await store.SearchAsync(Bm25Encoder.EncodeQuery("lake"), 10, "US", CancellationToken.None);
            IReadOnlyDictionary<string, DateTime> versions = await store.ListIndexedVersionsAsync(CancellationToken.None);
            await store.DeleteArticleAsync(articles[1].NewsId.ToUpperInvariant(), CancellationToken.None);
            ulong after = await store.CountAsync(CancellationToken.None);

            await Assert.That(keta[0].Title).IsEqualTo("Keta salmon run");
            await Assert.That(keta[0].Score).IsGreaterThan(keta[^1].Score);
            await Assert.That(us.Single().Title).IsEqualTo("Bass derby");
            await Assert.That(versions.Count).IsEqualTo(3);
            await Assert.That(after).IsEqualTo(2UL);

            IReadOnlyDictionary<string, uint[]> terms = await store.GetTermsAsync(
                [articles[0].NewsId, articles[1].NewsId], CancellationToken.None);
            uint walleye = Bm25Encoder.Hash(PorterStemmer.Stem("walleye"));
            await Assert.That(terms.Keys).IsEquivalentTo(new[] { articles[0].NewsId }); // [1] was deleted above
            await Assert.That(terms[articles[0].NewsId]).Contains(walleye);
            await Assert.That(terms[articles[0].NewsId].SequenceEqual(terms[articles[0].NewsId].Order())).IsTrue();
        }
        finally
        {
            if (await client.CollectionExistsAsync(collection))
            {
                await client.DeleteCollectionAsync(collection);
            }
        }
    }
}
