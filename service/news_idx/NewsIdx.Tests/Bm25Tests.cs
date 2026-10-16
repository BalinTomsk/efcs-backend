using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using NewsIdx.Configuration;
using NewsIdx.Indexing;
using NewsIdx.Search;
using TUnit.Core;

namespace NewsIdx.Tests;

/// <summary>BM25: analysis, document weights, ranking, the sync filling a new index, and the HTTP route.</summary>
public class Bm25Tests
{
    [Test]
    [Arguments("fishing", "fish")]
    [Arguments("fished", "fish")]
    [Arguments("fishes", "fish")]
    [Arguments("caresses", "caress")]
    [Arguments("ponies", "poni")]
    [Arguments("hopping", "hop")]
    [Arguments("relational", "relat")]
    [Arguments("generalization", "gener")]
    [Arguments("regulations", "regul")]
    [Arguments("walleye", "walley")]
    [Arguments("trout", "trout")]
    [Arguments("as", "as")]
    [Arguments("2026", "2026")]
    public async Task PorterStemmer_MatchesTheReferenceAlgorithm(string word, string stem) =>
        await Assert.That(PorterStemmer.Stem(word)).IsEqualTo(stem);

    [Test]
    public async Task Terms_FoldAccents_DropStopWords_AndStem()
    {
        string[] terms = Bm25Encoder.Terms("The Rivière-du-Loup anglers are FISHING for trout!").ToArray();

        await Assert.That(terms).IsEquivalentTo(new[] { "rivier", "du", "loup", "angler", "fish", "trout" });
    }

    [Test]
    public async Task EncodeDocument_SaturatesRepeats_AndWeightsTheTitle()
    {
        var encoder = new Bm25Encoder(new Bm25Options());

        SparseTerms doc = encoder.EncodeDocument("Pike", "trout trout trout trout trout trout trout trout bass");
        float Weight(string term) => doc.Values[Array.IndexOf(doc.Indices, Bm25Encoder.Hash(PorterStemmer.Stem(term)))];

        await Assert.That(doc.Indices).IsEquivalentTo(doc.Indices.Distinct().Order());
        await Assert.That(Weight("trout")).IsGreaterThan(Weight("bass"));
        await Assert.That(Weight("trout")).IsLessThan(8 * Weight("bass")); // k1 saturation
        await Assert.That(Weight("trout")).IsLessThan(2.2f);                // never above k1 + 1
        await Assert.That(Weight("pike")).IsGreaterThan(Weight("bass"));    // title counts twice
    }

    [Test]
    public async Task EncodeQuery_OnlyStopWords_IsEmpty() =>
        await Assert.That(Bm25Encoder.EncodeQuery("the and of").IsEmpty).IsTrue();

    [Test]
    public async Task Search_RareWordOutranksCommonOne()
    {
        (NewsSearchService service, _) = await Indexed();

        NewsSearchResult result = await service.SearchBm25Async("lake keta", 10, null, CancellationToken.None);

        // "lake" is in every article; "keta" in one, so IDF puts that one first.
        await Assert.That(result.Items[0].Title).IsEqualTo("Keta salmon run");
    }

    [Test]
    public async Task Search_MatchesOtherWordForms_AndFilters()
    {
        (NewsSearchService service, _) = await Indexed();

        NewsSearchResult stocked = await service.SearchBm25Async("stocked", 10, null, CancellationToken.None);
        NewsSearchResult us = await service.SearchBm25Async("tournament", 10, "us", CancellationToken.None);
        NewsSearchResult ca = await service.SearchBm25Async("tournament", 10, "ca", CancellationToken.None);
        NewsSearchResult none = await service.SearchBm25Async("the", 10, null, CancellationToken.None);

        await Assert.That(stocked.Items.Select(i => i.Title)).Contains("Trout stocking");
        await Assert.That(us.Items.Single().Title).IsEqualTo("Bass tournament");
        await Assert.That(ca.Items).IsEmpty();
        await Assert.That(none.Items).IsEmpty();
    }

    [Test]
    public async Task MatchSnippet_CentresOnTheMatch()
    {
        string body = string.Join(' ', Enumerable.Repeat("filler", 60)) + " the keta run started early this year";

        string snippet = NewsSearchService.MatchSnippet(body, "keta");

        await Assert.That(snippet).StartsWith("…");
        await Assert.That(snippet).Contains("keta run");
        await Assert.That(NewsSearchService.MatchSnippet("keta run", "keta")).IsEqualTo("keta run");
    }

    [Test]
    public async Task Sync_FillsAnEmptyKeywordIndex_AndPrunesIt()
    {
        var source = new FakeArticleSource();
        var store = new FakeVectorStore();
        source.Add("One", "first lake article");
        var gone = source.Add("Two", "second lake article");
        await TestSupport.Sync(source, store).RunAsync(full: true, CancellationToken.None); // vectors only

        var keywords = new FakeKeywordStore();
        SyncStatus fill = await TestSupport.Sync(source, store, keywords: keywords).RunAsync(full: false, CancellationToken.None);
        source.Published.TryRemove(gone.NewsId, out _);
        await TestSupport.Sync(source, store, keywords: keywords).RunAsync(full: false, CancellationToken.None);

        await Assert.That(fill.ToIndex).IsEqualTo(2);
        await Assert.That(keywords.Docs.Keys).IsEquivalentTo(store.Points.Values.Select(p => p.Article.NewsId).Distinct());
        await Assert.That(keywords.Docs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Bootstrap_EmptyKeywordIndex_BesideFilledVectors_RunsIncremental()
    {
        var source = new FakeArticleSource();
        var store = new FakeVectorStore();
        source.Add("One", "first lake article");
        await TestSupport.Sync(source, store).RunAsync(full: true, CancellationToken.None);
        var keywords = new FakeKeywordStore();
        NewsSyncService sync = TestSupport.Sync(source, store, keywords: keywords);
        var bootstrap = new IndexBootstrap(store, keywords, new FakeEmbedder(), sync,
            Options.Create(TestSupport.Options(o => o.Sync.IntervalMinutes = 0)), NullLogger<IndexBootstrap>.Instance);

        await bootstrap.StartAsync(CancellationToken.None);
        await bootstrap.ExecuteTask!;

        await Assert.That(keywords.Ensured).IsTrue();
        await Assert.That(keywords.Docs.Count).IsEqualTo(1);
        await Assert.That(sync.Status.Mode).IsEqualTo("incremental");
    }

    [Test]
    [NotInParallel("web-host")] // Serilog's bootstrap logger is static: one host at a time
    public async Task Http_Bm25_ReturnsEnvelope_AndBadInputIs400()
    {
        (_, FakeArticleSource source, FakeVectorStore store, FakeKeywordStore keywords) = await IndexedStores();
        await using WebApplicationFactory<Program> factory = SearchTests.Factory(source, store, keywords);
        HttpClient client = factory.CreateClient();

        HttpResponseMessage ok = await client.GetAsync("/api/v1/newsidx/bm25?q=keta&limit=2");
        JsonElement body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        HttpResponseMessage bad = await client.GetAsync("/api/v1/newsidx/bm25?q=keta&limit=0");

        await Assert.That(ok.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("data").GetProperty("items")[0].GetProperty("title").GetString()).IsEqualTo("Keta salmon run");
        await Assert.That(bad.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    private static async Task<(NewsSearchService Service, FakeArticleSource Source)> Indexed()
    {
        (NewsSearchService service, FakeArticleSource source, _, _) = await IndexedStores();
        return (service, source);
    }

    private static async Task<(NewsSearchService, FakeArticleSource, FakeVectorStore, FakeKeywordStore)> IndexedStores()
    {
        var source = new FakeArticleSource();
        var store = new FakeVectorStore();
        var keywords = new FakeKeywordStore();
        source.Add("Walleye limits change", "new walleye limits on the lake this season");
        source.Add("Bass tournament", "largemouth bass tournament results on the lake", country: "US");
        source.Add("Trout stocking", "rainbow trout stocking program on the lake in spring");
        source.Add("Keta salmon run", "keta salmon run reached the lake early");
        source.Add("Pike derby", "pike derby on the lake");
        await TestSupport.Sync(source, store, keywords: keywords).RunAsync(full: true, CancellationToken.None);
        var service = new NewsSearchService(new FakeEmbedder(), store, keywords, Options.Create(TestSupport.Options()));
        return (service, source, store, keywords);
    }
}
