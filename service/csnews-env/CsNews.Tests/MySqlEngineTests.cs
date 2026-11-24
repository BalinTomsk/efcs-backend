using System.Text.Json;
using CsNews.Configuration;
using CsNews.News;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;

namespace CsNews.Tests;

/// <summary>
/// The real <see cref="MySqlNewsStore"/> against a real MySQL engine, so every statement is parsed and bound the
/// way production does it (a fake never parses SQL). Runs only when <c>CSNEWS_TEST_MYSQL</c> holds a JDBC URL or
/// connection string of a THROWAWAY database loaded from <c>envfish-db/mysql/script01_createTable.sql</c>,
/// <c>script01_createView.sql</c> and <c>script02_Proc.sql</c> — it writes rows. Otherwise each test is a no-op:
/// <code>
/// docker run -d --name csnews-mysql -e MYSQL_ROOT_PASSWORD=probe -e MYSQL_DATABASE=envfish -p 13306:3306 mysql:8.0
/// CSNEWS_TEST_MYSQL="Server=127.0.0.1;Port=13306;Database=envfish;User ID=root;Password=probe"
/// </code>
/// </summary>
[NotInParallel]
public class MySqlEngineTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("CSNEWS_TEST_MYSQL");

    private static MySqlNewsStore Store() =>
        new(MySqlUrl.Build(Url!, null, null), new NewsDbGuard(NullLogger<NewsDbGuard>.Instance));

    private static NewsWrite Article(string title, byte[]? photo, string lake, string fish) => new(
        title, "Ann Author", "https://a.example", "Daily Bugle", "https://s.example", null,
        "First paragraph mentions 50% of muskie_hunters.", "Second paragraph.", null, "CA", "2026-11-20T08:30:00",
        lake, fish, null, null,
        new NewsPhotoWrite(photo is null ? null : Convert.ToBase64String(photo), "Photo Credit", "a pike"),
        null, null);

    [Test]
    public async Task ReadsAndWrites_RoundTrip()
    {
        if (Url is null)
        {
            return;
        }
        MySqlNewsStore store = Store();
        CancellationToken ct = CancellationToken.None;
        string lake = Guid.NewGuid().ToString();
        string fish = Guid.NewGuid().ToString();
        byte[] photo = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();

        await store.PingAsync(ct);

        string id = await store.InsertAsync(Article("Engine test pike " + lake[..8], photo, lake, fish), ct);
        await Assert.That(id).IsNotEmpty();

        JsonElement? doc = await store.GetDocumentAsync(id, ct);
        await Assert.That(doc).IsNotNull();
        JsonElement? export = await store.ExportAsync(id, ct);
        await Assert.That(export).IsNotNull();
        await Assert.That(await store.GetDocumentAsync(Guid.NewGuid().ToString(), ct)).IsNull();

        byte[]? stored = await store.PhotoAsync(id, ct);
        await Assert.That(stored).IsEquivalentTo(photo);

        NewsListPage list = await store.ListAsync(null, 0, 20, NewsListOrder.Date, ct);
        await Assert.That(list.Items.Select(i => i.NewsId)).Contains(id);
        NewsListPage edited = await store.ListAsync("CA", 0, 20, NewsListOrder.Edited, ct);
        await Assert.That(edited.Total).IsGreaterThan(0);

        // Literal % and _ are escaped: "50% of muskie_" matches, "50x of" does not.
        NewsSearchPage hit = await store.SearchAsync(new NewsSearchQuery("50% of muskie_", [], "CA", 0, 10), ct);
        await Assert.That(hit.Items.Select(i => i.NewsId)).Contains(id);
        NewsSearchPage miss = await store.SearchAsync(new NewsSearchQuery("50x of", [], null, 0, 10), ct);
        await Assert.That(miss.Items.Select(i => i.NewsId)).DoesNotContain(id);
        NewsSearchPage byFish = await store.SearchAsync(new NewsSearchQuery("zzz-no-text-match", [fish, "other"], null, 0, 10), ct);
        await Assert.That(byFish.Items.Select(i => i.NewsId)).Contains(id);
        await Assert.That(byFish.Items.First(i => i.NewsId == id).FishIds).IsEquivalentTo(new[] { fish });

        await Assert.That((await store.LakeAsync(lake, 10, ct)).Items.Select(i => i.NewsId)).Contains(id);
        await Assert.That((await store.FishAsync(fish, 10, ct)).Items.Select(i => i.NewsId)).Contains(id);
        await Assert.That((await store.TaggedAsync(2000, ct)).Select(i => i.NewsId)).Contains(id);

        NewsDefaultPage home = await store.DefaultAsync(ct);
        await Assert.That(home.Items.Count).IsGreaterThan(0);

        await Assert.That(await store.UpdateAsync(id, Article("Engine test pike, edited", null, lake, fish), ct)).IsTrue();
        await Assert.That(await store.UpdateAsync(Guid.NewGuid().ToString(), Article("x", null, lake, fish), ct)).IsFalse();
        await Assert.That(await store.PhotoAsync(id, ct)).IsEquivalentTo(photo); // null slot-0 bytes keep the photo
    }

    [Test]
    public async Task AdminDraftPublishPhoto()
    {
        if (Url is null)
        {
            return;
        }
        MySqlNewsStore store = Store();
        CancellationToken ct = CancellationToken.None;

        string draft = await store.CreateDraftAsync(ct);
        await Assert.That(draft.Length).IsEqualTo(36);
        await Assert.That(await store.PhotoAsync(draft, ct)).IsNull(); // a draft's photo is never served

        NewsPublishResult published = await store.PublishAsync(new NewsPublishRequest(draft, "Published draft", "A", "S",
            null, null, "2026-11-21T10:00:00", null, "p0", null, null, "US", null, null, null, null), ct);
        await Assert.That(published).IsEqualTo(new NewsPublishResult(draft, "updated"));

        NewsPhotoUpdateResult photo = await store.UpdatePhotoAsync(draft, 0, new byte[200], "credit", null, ct);
        await Assert.That(photo).IsEqualTo(new NewsPhotoUpdateResult(true, true));
        NewsPhotoUpdateResult unknown = await store.UpdatePhotoAsync(Guid.NewGuid().ToString(), 1, new byte[10], null, null, ct);
        await Assert.That(unknown.Found).IsFalse();
        await Assert.That((await store.PhotoAsync(draft, ct))!.Length).IsEqualTo(200);
    }
}
