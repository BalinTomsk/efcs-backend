using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CsNews.News;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;

namespace CsNews.Tests;

/// <summary>The news data API (<c>/api/v1/csnews/*</c>) over a fake store: routes, shapes, status codes.</summary>
public class NewsDataTests
{
    [Test]
    public async Task Doc_Found_IsTheRawDocument_Unknown_Is404()
    {
        var store = new FakeStore { Doc = JsonDocument.Parse("""{"news_id":"n1","title":"Pike"}""").RootElement };
        await using WebApplicationFactory<Program> factory = Factory(store);
        HttpClient client = factory.CreateClient();

        JsonElement ok = await client.GetFromJsonAsync<JsonElement>("/api/v1/csnews/doc/n1");
        store.Doc = null;
        HttpResponseMessage missing = await client.GetAsync("/api/v1/csnews/doc/n2");
        JsonElement missingBody = await missing.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(ok.GetProperty("data").GetProperty("title").GetString()).IsEqualTo("Pike");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(missingBody.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("not_found");
    }

    [Test]
    public async Task List_PassesWindowCountryAndOrder_InCamelCase()
    {
        var store = new FakeStore();
        await using WebApplicationFactory<Program> factory = Factory(store);

        JsonElement body = await factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/api/v1/csnews/list?country=us&offset=20&limit=10&order=edited");

        await Assert.That(store.LastList).IsEqualTo(("US", 20, 10, NewsListOrder.Edited));
        JsonElement item = body.GetProperty("data").GetProperty("items")[0];
        await Assert.That(item.GetProperty("newsId").GetString()).IsEqualTo("n1");
        await Assert.That(item.GetProperty("hasPhoto").GetBoolean()).IsTrue();
        await Assert.That(item.GetProperty("blockOrd").GetInt32()).IsEqualTo(0);
        await Assert.That(body.GetProperty("data").GetProperty("total").GetInt64()).IsEqualTo(42);
    }

    [Test]
    [Arguments("/api/v1/csnews/list?limit=0")]
    [Arguments("/api/v1/csnews/list?limit=101")]
    [Arguments("/api/v1/csnews/list?offset=-1")]
    [Arguments("/api/v1/csnews/list?country=CAN")]
    [Arguments("/api/v1/csnews/list?order=random")]
    [Arguments("/api/v1/csnews/search?q=")]
    [Arguments("/api/v1/csnews/tagged?limit=5001")]
    public async Task BadInput_Is400_AndNeverReachesTheStore(string url)
    {
        var store = new FakeStore();
        await using WebApplicationFactory<Program> factory = Factory(store);
        HttpResponseMessage r = await factory.CreateClient().GetAsync(url);

        await Assert.That(r.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(store.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task MalformedJsonBody_IsOurEnvelope_NotProblemDetails()
    {
        var store = new FakeStore();
        await using WebApplicationFactory<Program> factory = Factory(store);
        HttpResponseMessage r = await factory.CreateClient().PostAsync("/api/v1/csnews/doc",
            new StringContent("{\"title\":\"x\"", System.Text.Encoding.UTF8, "application/json"));
        JsonElement body = await r.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(r.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_request");
        await Assert.That(store.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Search_RepeatedFishIds_AreDistinct()
    {
        var store = new FakeStore();
        await using WebApplicationFactory<Program> factory = Factory(store);

        HttpResponseMessage r = await factory.CreateClient()
            .GetAsync("/api/v1/csnews/search?q=%20walleye%20&fishId=f1&fishId=f2&fishId=f1&offset=0&limit=20");

        await Assert.That(r.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(store.LastSearch!.Query).IsEqualTo("walleye");
        await Assert.That(store.LastSearch.FishIds).IsEquivalentTo(new[] { "f1", "f2" });
        await Assert.That(store.LastSearch.Country).IsNull();
    }

    [Test]
    public async Task Photo_IsRawBytes_Missing_Is404()
    {
        var store = new FakeStore { Photo = [1, 2, 3] };
        await using WebApplicationFactory<Program> factory = Factory(store);
        HttpClient client = factory.CreateClient();

        HttpResponseMessage ok = await client.GetAsync("/api/v1/csnews/photo/n1");
        byte[] bytes = await ok.Content.ReadAsByteArrayAsync();
        store.Photo = null;
        HttpResponseMessage missing = await client.GetAsync("/api/v1/csnews/photo/n1");

        await Assert.That(ok.Content.Headers.ContentType!.MediaType).IsEqualTo("application/octet-stream");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Insert_Is201WithId_Update_UnknownIs404()
    {
        var store = new FakeStore();
        await using WebApplicationFactory<Program> factory = Factory(store);
        HttpClient client = factory.CreateClient();
        var write = new
        {
            title = "Big pike",
            country = "CA",
            stamp = "2026-11-20T08:30:00",
            photo0 = new { data = Convert.ToBase64String([9, 9]), author = "Ann", alt = "pike" },
        };

        HttpResponseMessage created = await client.PostAsJsonAsync("/api/v1/csnews/doc", write);
        JsonElement createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        store.UpdateFound = false;
        HttpResponseMessage unknown = await client.PutAsJsonAsync("/api/v1/csnews/doc/nx", write);
        HttpResponseMessage blank = await client.PostAsJsonAsync("/api/v1/csnews/doc", new { title = " " });

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(createdBody.GetProperty("data").GetProperty("newsId").GetString()).IsEqualTo("new-id");
        await Assert.That(store.LastWrite!.Photo0!.Author).IsEqualTo("Ann");
        await Assert.That(store.LastWrite.Stamp).IsEqualTo("2026-11-20T08:30:00");
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(blank.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task AdminRoutes_DraftPublishPhoto()
    {
        var store = new FakeStore();
        await using WebApplicationFactory<Program> factory = Factory(store);
        HttpClient client = factory.CreateClient();

        JsonElement draft = await (await client.PostAsync("/api/v1/csnews/admin/draft", null)).Content.ReadFromJsonAsync<JsonElement>();
        JsonElement published = await (await client.PostAsJsonAsync("/api/v1/csnews/admin/publish",
            new { newsId = "d1", title = "T", stamp = (string?)null })).Content.ReadFromJsonAsync<JsonElement>();
        HttpResponseMessage photo = await client.PutAsJsonAsync("/api/v1/csnews/admin/d1/photo/2",
            new { data = Convert.ToBase64String([7]), author = (string?)null, alt = "a" });
        HttpResponseMessage badSlot = await client.PutAsJsonAsync("/api/v1/csnews/admin/d1/photo/3",
            new { data = Convert.ToBase64String([7]) });
        HttpResponseMessage badBase64 = await client.PutAsJsonAsync("/api/v1/csnews/admin/d1/photo/0",
            new { data = "not base64!" });

        await Assert.That(draft.GetProperty("data").GetProperty("newsId").GetString()).IsEqualTo("draft-id");
        await Assert.That(published.GetProperty("data").GetProperty("action").GetString()).IsEqualTo("updated");
        await Assert.That(photo.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(store.LastPhoto).IsEqualTo(("d1", 2, 1, (string?)null, (string?)"a"));
        await Assert.That(badSlot.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(badBase64.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task DatabaseDown_Is503WithRetryAfter_NeverA500()
    {
        var store = new FakeStore { Down = true };
        await using WebApplicationFactory<Program> factory = Factory(store);
        HttpClient client = factory.CreateClient();

        HttpResponseMessage list = await client.GetAsync("/api/v1/csnews/list");
        HttpResponseMessage ping = await client.GetAsync("/api/v1/csnews/ping");

        await Assert.That(list.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(list.Headers.Contains("Retry-After")).IsTrue();
        await Assert.That(ping.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task NoMySqlConfigured_NewsRoutesAre503_SearchStillServes()
    {
        await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage news = await client.GetAsync("/api/v1/csnews/default");
        HttpResponseMessage sources = await client.GetAsync("/api/v1/newssearch/sources");

        await Assert.That(news.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(sources.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task SqlHelpers()
    {
        await Assert.That(MySqlNewsStore.EscapeLike(@"50%_a\b[c]")).IsEqualTo(@"50\%\_a\\b[c]");
        await Assert.That(MySqlNewsStore.FishClause(0)).IsEqualTo(string.Empty);
        await Assert.That(MySqlNewsStore.FishClause(2))
            .IsEqualTo("OR fish1_id IN (@f0, @f1) OR fish2_id IN (@f0, @f1) OR fish3_id IN (@f0, @f1)");
        await Assert.That(MySqlNewsStore.DistinctIds("a", null, " ", "a", "b")).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(MySqlNewsStore.Stamp("2026-11-20T08:30:15.250")).IsEqualTo(new DateTime(2026, 11, 20, 8, 30, 15, 250));
        await Assert.That(MySqlNewsStore.Stamp(null)).IsNull();
        await Assert.That(() => MySqlNewsStore.Stamp("yesterday")).Throws<NewsRequestException>();
        // Raw string: the SQL engine must see ESCAPE '\\' (one backslash once MySQL unescapes the literal).
        await Assert.That(MySqlNewsStore.SearchFromWhere.Contains(@"ESCAPE '\\'")).IsTrue();
    }

    private static WebApplicationFactory<Program> Factory(FakeStore store) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(services => services.AddSingleton<INewsStore>(store)));

    internal sealed class FakeStore : INewsStore
    {
        public int Calls;
        public bool Down;
        public JsonElement? Doc;
        public byte[]? Photo;
        public bool UpdateFound = true;
        public (string?, int, int, NewsListOrder)? LastList;
        public NewsSearchQuery? LastSearch;
        public NewsWrite? LastWrite;
        public (string, int, int, string?, string?)? LastPhoto;

        private void Hit()
        {
            Calls++;
            if (Down)
            {
                throw new NewsStoreUnavailableException("down");
            }
        }

        public Task<JsonElement?> GetDocumentAsync(string id, CancellationToken ct) { Hit(); return Task.FromResult(Doc); }
        public Task<JsonElement?> ExportAsync(string id, CancellationToken ct) { Hit(); return Task.FromResult(Doc); }

        public Task<NewsListPage> ListAsync(string? country, int offset, int limit, NewsListOrder order, CancellationToken ct)
        {
            Hit();
            LastList = (country, offset, limit, order);
            return Task.FromResult(new NewsListPage([new NewsListItem(1, "n1", "T", "S", "2026-11-01", "CA.png", true, 0)], 42, offset, limit));
        }

        public Task<NewsDefaultPage> DefaultAsync(CancellationToken ct) { Hit(); return Task.FromResult(new NewsDefaultPage([])); }
        public Task<byte[]?> PhotoAsync(string id, CancellationToken ct) { Hit(); return Task.FromResult(Photo); }

        public Task<NewsSearchPage> SearchAsync(NewsSearchQuery query, CancellationToken ct)
        {
            Hit();
            LastSearch = query;
            return Task.FromResult(new NewsSearchPage([], 0, query.Query, query.Offset, query.Limit));
        }

        public Task<NewsLakePage> LakeAsync(string lakeId, int limit, CancellationToken ct) { Hit(); return Task.FromResult(new NewsLakePage(lakeId, limit, [])); }
        public Task<NewsFishPage> FishAsync(string fishId, int limit, CancellationToken ct) { Hit(); return Task.FromResult(new NewsFishPage(fishId, limit, [])); }
        public Task<IReadOnlyList<TaggedNews>> TaggedAsync(int limit, CancellationToken ct) { Hit(); return Task.FromResult<IReadOnlyList<TaggedNews>>([]); }

        public Task<string> InsertAsync(NewsWrite write, CancellationToken ct) { Hit(); LastWrite = write; return Task.FromResult("new-id"); }
        public Task<bool> UpdateAsync(string id, NewsWrite write, CancellationToken ct) { Hit(); LastWrite = write; return Task.FromResult(UpdateFound); }
        public Task<string> CreateDraftAsync(CancellationToken ct) { Hit(); return Task.FromResult("draft-id"); }
        public Task<NewsPublishResult> PublishAsync(NewsPublishRequest request, CancellationToken ct) { Hit(); return Task.FromResult(new NewsPublishResult(request.NewsId, "updated")); }

        public Task<NewsPhotoUpdateResult> UpdatePhotoAsync(string id, int index, byte[] photo, string? author, string? alt, CancellationToken ct)
        {
            Hit();
            LastPhoto = (id, index, photo.Length, author, alt);
            return Task.FromResult(new NewsPhotoUpdateResult(true, true));
        }

        public Task PingAsync(CancellationToken ct) { Hit(); return Task.CompletedTask; }
    }
}
