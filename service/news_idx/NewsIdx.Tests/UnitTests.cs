using NewsIdx.Configuration;
using NewsIdx.Domain;
using NewsIdx.Indexing;
using TUnit.Core;

namespace NewsIdx.Tests;

/// <summary>Chunking, ids, event parsing and connection-string conversion.</summary>
public class UnitTests
{
    [Test]
    public async Task Chunk_ShortArticle_IsOnePassage_StartingWithTitle()
    {
        var article = FakeArticleSource.Article(Guid.NewGuid().ToString(), "Pike record", "A <b>big</b> pike &amp; a small boat.");

        IReadOnlyList<string> chunks = NewsChunker.Chunk(article, new ChunkingOptions());

        await Assert.That(chunks.Count).IsEqualTo(1);
        await Assert.That(chunks[0]).IsEqualTo("Pike record\nA big pike & a small boat.");
    }

    [Test]
    public async Task Chunk_LongArticle_OverlapsAndCoversEveryWord()
    {
        var article = FakeArticleSource.Article(Guid.NewGuid().ToString(), "T", string.Join(' ', Enumerable.Range(0, 500).Select(i => $"w{i}")));

        IReadOnlyList<string> chunks = NewsChunker.Chunk(article, new ChunkingOptions { ChunkWords = 200, OverlapWords = 50 });

        // steps of 150: 0-199, 150-349, 300-499
        await Assert.That(chunks.Count).IsEqualTo(3);
        await Assert.That(chunks.All(c => c.StartsWith("T\n"))).IsTrue();
        await Assert.That(chunks[1]).StartsWith("T\nw150 ");
        await Assert.That(chunks[2]).EndsWith("w499");
    }

    [Test]
    public async Task Chunk_NoBody_IsTitleOnly()
    {
        var article = FakeArticleSource.Article(Guid.NewGuid().ToString(), "Only a title", "   ");
        await Assert.That(NewsChunker.Chunk(article, new ChunkingOptions())).IsEquivalentTo(new[] { "Only a title" });
    }

    [Test]
    public async Task PointId_IsDeterministic_CaseInsensitive_AndPerChunk()
    {
        string id = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
        await Assert.That(NewsIds.PointId(id, 0)).IsEqualTo(NewsIds.PointId(id.ToLowerInvariant(), 0));
        await Assert.That(NewsIds.PointId(id, 0)).IsNotEqualTo(NewsIds.PointId(id, 1));
        await Assert.That(NewsIds.Normalize(id)).IsEqualTo("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    }

    [Test]
    public async Task Event_Valid_IsParsed_WithNormalizedId()
    {
        NewsEvent? evt = NewsEvent.TryParse(TestSupport.Event("6F9619FF-8B86-D011-B42D-00C04FC964FF", "Published"), out string? error);

        await Assert.That(error).IsNull();
        await Assert.That(evt!.NewsId).IsEqualTo("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        await Assert.That(evt.Action).IsEqualTo("published");
        await Assert.That(evt.Attempt).IsEqualTo(1);
    }

    [Test]
    [Arguments("not json")]
    [Arguments("[1,2]")]
    [Arguments("{\"schema\":\"fishfind.account-event.v1\",\"eventType\":\"fishfind.news.article\",\"aggregateId\":\"6f9619ff-8b86-d011-b42d-00c04fc964ff\"}")]
    [Arguments("{\"schema\":\"fishfind.news-event.v1\",\"eventType\":\"fishfind.account.user\",\"aggregateId\":\"6f9619ff-8b86-d011-b42d-00c04fc964ff\"}")]
    [Arguments("{\"schema\":\"fishfind.news-event.v1\",\"eventType\":\"fishfind.news.article\",\"aggregateId\":\"nope\"}")]
    public async Task Event_Unusable_IsRejected(string body)
    {
        await Assert.That(NewsEvent.TryParse(body, out string? error)).IsNull();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Event_WithAttempt_RoundTrips()
    {
        string body = NewsEvent.WithAttempt(TestSupport.Event(Guid.NewGuid().ToString()), 3);
        await Assert.That(NewsEvent.TryParse(body, out _)!.Attempt).IsEqualTo(3);
    }

    [Test]
    public async Task MySqlUrl_Jdbc_IsConverted()
    {
        string cs = MySqlUrl.Build("jdbc:mysql://db.example.com:3307/envfish?useSSL=true&serverTimezone=UTC", "portos", "p@ss");
        var b = new MySqlConnector.MySqlConnectionStringBuilder(cs);

        await Assert.That(b.Server).IsEqualTo("db.example.com");
        await Assert.That(b.Port).IsEqualTo(3307u);
        await Assert.That(b.Database).IsEqualTo("envfish");
        await Assert.That(b.UserID).IsEqualTo("portos");
        await Assert.That(b.Password).IsEqualTo("p@ss");
        await Assert.That(b.SslMode).IsEqualTo(MySqlConnector.MySqlSslMode.Required);
        await Assert.That(b.GuidFormat).IsEqualTo(MySqlConnector.MySqlGuidFormat.None); // CHAR(36) stays a string
    }

    [Test]
    public async Task MySqlUrl_Native_KeepsItsOwnCredentials()
    {
        string cs = MySqlUrl.Build("Server=h;Database=d;User ID=u;Password=x", "other", "other");
        var b = new MySqlConnector.MySqlConnectionStringBuilder(cs);
        await Assert.That(b.UserID).IsEqualTo("u");
        await Assert.That(b.Password).IsEqualTo("x");
    }

    [Test]
    [Arguments("/admin/sync", true)]
    [Arguments("/admin", true)]
    [Arguments("/metrics", true)]
    [Arguments("/administrator", false)]
    [Arguments("/api/v1/newsidx/search", false)]
    [Arguments("/health", false)]
    public async Task ManagementPaths_AreRecognised(string path, bool management)
    {
        await Assert.That(Program.IsManagementPath(path)).IsEqualTo(management);
    }
}
