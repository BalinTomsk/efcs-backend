using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewsFind.Data;
using NewsFind.Embedding;
using NewsFind.Search;
using TUnit.Core;

namespace NewsFind.Tests;

/// <summary>Search validation and grouping, plus the HTTP surface end to end over fakes.</summary>
public class SearchTests
{
    private static async Task<(NewsSearchService Service, FakeArticleSource Source)> Indexed()
    {
        var source = new FakeArticleSource();
        var store = new FakeVectorStore();
        source.Add("Walleye limits change", TestSupport.Words(600, "walleye") + " walleye limits regulation");
        source.Add("Bass tournament", "largemouth bass tournament results on the lake", country: "US");
        source.Add("Trout stocking", "rainbow trout stocking program spring");
        await TestSupport.Sync(source, store).RunAsync(full: true, CancellationToken.None);
        var service = new NewsSearchService(new FakeEmbedder(), store, Options.Create(TestSupport.Options()));
        return (service, source);
    }

    [Test]
    public async Task Search_RanksTheMatchingArticleFirst_OneHitPerArticle()
    {
        (NewsSearchService service, _) = await Indexed();

        NewsSearchResult result = await service.SearchAsync("bass tournament", 10, null, CancellationToken.None);

        await Assert.That(result.Items[0].Title).IsEqualTo("Bass tournament");
        await Assert.That(result.Items.Select(i => i.NewsId).Distinct().Count()).IsEqualTo(result.Items.Count);
    }

    [Test]
    public async Task Search_CountryFilter_Applies()
    {
        (NewsSearchService service, _) = await Indexed();

        NewsSearchResult result = await service.SearchAsync("bass tournament", 10, "ca", CancellationToken.None);

        await Assert.That(result.Country).IsEqualTo("CA");
        await Assert.That(result.Items.All(i => i.Country == "CA")).IsTrue();
    }

    [Test]
    [Arguments(null, 10, null)]
    [Arguments("  ", 10, null)]
    [Arguments("ok", 0, null)]
    [Arguments("ok", 51, null)]
    [Arguments("ok", 10, "CAN")]
    [Arguments("ok", 10, "C1")]
    public async Task Search_BadInput_IsInvalidRequest(string? q, int limit, string? country)
    {
        (NewsSearchService service, _) = await Indexed();
        await Assert.That(async () => await service.SearchAsync(q, limit, country, CancellationToken.None))
            .Throws<InvalidRequestException>();
    }

    [Test]
    public async Task Snippet_DropsTitle_AndCutsAtAWord()
    {
        string snippet = NewsSearchService.Snippet("Title\n" + string.Join(' ', Enumerable.Repeat("word", 100)), "Title");
        await Assert.That(snippet).DoesNotStartWith("Title");
        await Assert.That(snippet.Length).IsLessThanOrEqualTo(241);
        await Assert.That(snippet).EndsWith("…");
    }

    [Test]
    public async Task Http_Search_ReturnsEnvelope_AndBadInputIs400()
    {
        (NewsSearchService _, FakeArticleSource source) = await Indexed();
        var store = new FakeVectorStore();
        await TestSupport.Sync(source, store).RunAsync(full: true, CancellationToken.None);
        await using WebApplicationFactory<Program> factory = Factory(source, store);
        HttpClient client = factory.CreateClient();

        HttpResponseMessage ok = await client.GetAsync("/api/v1/newsfind/search?q=trout%20stocking&limit=2");
        JsonElement body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        HttpResponseMessage bad = await client.GetAsync("/api/v1/newsfind/search?q=");
        HttpResponseMessage admin = await client.PostAsync("/admin/sync", null);

        await Assert.That(ok.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("data").GetProperty("items")[0].GetProperty("title").GetString()).IsEqualTo("Trout stocking");
        await Assert.That(bad.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(admin.StatusCode).IsEqualTo(HttpStatusCode.NotFound); // management path, public port
    }

    private static WebApplicationFactory<Program> Factory(FakeArticleSource source, FakeVectorStore store) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("NewsFind:Sync:OnStartup", "Never");
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<INewsArticleSource>(source);
                services.AddSingleton<ITextEmbedder>(new FakeEmbedder());
                services.AddSingleton<IVectorStore>(store);
            });
        });
}
