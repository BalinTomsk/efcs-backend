using Microsoft.Extensions.Logging.Abstractions;
using NewsIdx.Configuration;
using NewsIdx.Domain;
using NewsIdx.Indexing;
using NewsIdx.Messaging;
using NewsIdx.Search;
using TUnit.Core;

namespace NewsIdx.Tests;

/// <summary>Indexer, sync (initial load / reconcile) and the event consumer, over fakes.</summary>
public class IndexingTests
{
    [Test]
    public async Task Index_PublishedArticle_StoresItsChunks()
    {
        var source = new FakeArticleSource();
        var store = new FakeVectorStore();
        NewsArticle a = source.Add("Walleye", TestSupport.Words(500));

        IndexOutcome outcome = await TestSupport.Indexer(source, store).IndexAsync(a.NewsId, CancellationToken.None);

        await Assert.That(outcome.Indexed).IsTrue();
        await Assert.That(outcome.Chunks).IsGreaterThan(1);
        await Assert.That(store.ChunksOf(a.NewsId)).IsEqualTo(outcome.Chunks);
    }

    [Test]
    public async Task Index_ShorterEdit_TrimsLeftoverChunks()
    {
        var source = new FakeArticleSource();
        var store = new FakeVectorStore();
        NewsArticle a = source.Add("Walleye", TestSupport.Words(800));
        NewsIndexer indexer = TestSupport.Indexer(source, store);
        await indexer.IndexAsync(a.NewsId, CancellationToken.None);

        source.Published[a.NewsId] = a with { Paragraphs = ["now short"] };
        await indexer.IndexAsync(a.NewsId, CancellationToken.None);

        await Assert.That(store.ChunksOf(a.NewsId)).IsEqualTo(1);
    }

    [Test]
    public async Task Index_UnpublishedArticle_IsRemoved()
    {
        var source = new FakeArticleSource();
        var store = new FakeVectorStore();
        NewsArticle a = source.Add("Walleye", "text");
        NewsIndexer indexer = TestSupport.Indexer(source, store);
        await indexer.IndexAsync(a.NewsId, CancellationToken.None);

        source.Published.TryRemove(a.NewsId, out _); // unpublished or deleted in MySQL
        IndexOutcome outcome = await indexer.IndexAsync(a.NewsId, CancellationToken.None);

        await Assert.That(outcome.Indexed).IsFalse();
        await Assert.That(store.ChunksOf(a.NewsId)).IsEqualTo(0);
    }

    [Test]
    public async Task FullSync_IndexesEveryPublishedArticle_AndPrunesTheRest()
    {
        var source = new FakeArticleSource();
        var store = new FakeVectorStore();
        NewsArticle a = source.Add("A", "alpha");
        NewsArticle b = source.Add("B", "beta");
        NewsArticle gone = FakeArticleSource.Article(Guid.NewGuid().ToString("D"), "Gone", "old");
        await store.ReplaceArticleAsync(gone, [new IndexedChunk(0, "x", new float[64])], "fake", CancellationToken.None);

        SyncStatus status = await TestSupport.Sync(source, store).RunAsync(full: true, CancellationToken.None);

        await Assert.That(status.Indexed).IsEqualTo(2);
        await Assert.That(status.Removed).IsEqualTo(1);
        await Assert.That(store.ChunksOf(a.NewsId) + store.ChunksOf(b.NewsId)).IsEqualTo(2);
        await Assert.That(store.ChunksOf(gone.NewsId)).IsEqualTo(0);
    }

    [Test]
    public async Task IncrementalSync_IndexesOnlyMissingOrChanged()
    {
        var source = new FakeArticleSource();
        var store = new FakeVectorStore();
        NewsArticle same = source.Add("Same", "unchanged");
        NewsArticle edited = source.Add("Edited", "before");
        NewsSyncService sync = TestSupport.Sync(source, store);
        await sync.RunAsync(full: true, CancellationToken.None);

        source.Published[edited.NewsId] = edited with { Paragraphs = ["after"], LastEditUtc = edited.LastEditUtc.AddMinutes(5) };
        NewsArticle added = source.Add("New", "fresh");
        source.Reads = 0;

        SyncStatus status = await sync.RunAsync(full: false, CancellationToken.None);

        await Assert.That(status.ToIndex).IsEqualTo(2);
        await Assert.That(source.Reads).IsEqualTo(2); // `same` was never re-read
        await Assert.That(store.ChunksOf(added.NewsId)).IsEqualTo(1);
    }

    [Test]
    public async Task IncrementalSync_SubSecondStamp_IsNotSeenAsChanged()
    {
        var source = new FakeArticleSource();
        var store = new FakeVectorStore();
        source.Add("Micro", "text", edited: new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234567));
        NewsSyncService sync = TestSupport.Sync(source, store);
        await sync.RunAsync(full: true, CancellationToken.None);

        SyncStatus status = await sync.RunAsync(full: false, CancellationToken.None);

        await Assert.That(status.ToIndex).IsEqualTo(0);
    }

    [Test]
    public async Task Sync_EmptyDatabase_NeverWipesTheIndex()
    {
        var source = new FakeArticleSource();
        var store = new FakeVectorStore();
        NewsArticle a = source.Add("A", "alpha");
        NewsSyncService sync = TestSupport.Sync(source, store);
        await sync.RunAsync(full: true, CancellationToken.None);

        source.Published.Clear();
        SyncStatus status = await sync.RunAsync(full: false, CancellationToken.None);

        await Assert.That(status.PruneSkipped).IsTrue();
        await Assert.That(store.ChunksOf(a.NewsId)).IsEqualTo(1);
    }

    [Test]
    public async Task Sync_OneBadArticle_DoesNotStopTheRun()
    {
        var source = new FakeArticleSource();
        var store = new FakeVectorStore();
        source.Add("A", "alpha");
        source.Add("B", "beta");
        var indexer = new FailingIndexer(source, store, failOn: source.Published.Keys.First());

        SyncStatus status = await TestSupport.Sync(source, store, indexer).RunAsync(full: true, CancellationToken.None);

        await Assert.That(status.Indexed).IsEqualTo(1);
        await Assert.That(status.Failed).IsEqualTo(1);
    }

    [Test]
    public async Task Sync_IsSingleFlight()
    {
        var source = new FakeArticleSource();
        var store = new FakeVectorStore();
        var gate = new TaskCompletionSource();
        var indexer = new FailingIndexer(source, store, failOn: null, wait: gate.Task);
        source.Add("A", "alpha");
        NewsSyncService sync = TestSupport.Sync(source, store, indexer);

        await Assert.That(sync.TryStart(full: true, CancellationToken.None)).IsTrue();
        await Assert.That(sync.TryStart(full: true, CancellationToken.None)).IsFalse();
        gate.SetResult();
    }

    [Test]
    public async Task Consumer_IndexesEventArticle_AndCoalescesDuplicates()
    {
        var source = new FakeArticleSource();
        var store = new FakeVectorStore();
        var queue = new FakeQueue();
        NewsArticle a = source.Add("Walleye", "text");
        queue.Pending.Enqueue(TestSupport.Event(a.NewsId, "created"));
        queue.Pending.Enqueue(TestSupport.Event(a.NewsId.ToUpperInvariant(), "updated"));
        queue.Pending.Enqueue("garbage");

        bool healthy = await Consumer(queue, TestSupport.Indexer(source, store)).PollOnceAsync(CancellationToken.None);

        await Assert.That(healthy).IsTrue();
        await Assert.That(source.Reads).IsEqualTo(1);
        await Assert.That(store.ChunksOf(a.NewsId)).IsEqualTo(1);
        await Assert.That(queue.Published).IsEmpty();
    }

    [Test]
    public async Task Consumer_Failure_RequeuesWithNextAttempt()
    {
        var source = new FakeArticleSource { Failure = new TimeoutException("mysql down") };
        var queue = new FakeQueue();
        string id = Guid.NewGuid().ToString("D");
        queue.Pending.Enqueue(TestSupport.Event(id));

        bool healthy = await Consumer(queue, TestSupport.Indexer(source, new FakeVectorStore())).PollOnceAsync(CancellationToken.None);

        await Assert.That(healthy).IsFalse();
        await Assert.That(queue.Published.Count).IsEqualTo(1);
        await Assert.That(NewsEvent.TryParse(queue.Published[0], out _)!.Attempt).IsEqualTo(2);
    }

    [Test]
    public async Task Consumer_LastAttempt_IsDropped()
    {
        var source = new FakeArticleSource { Failure = new InvalidOperationException("boom") };
        var queue = new FakeQueue();
        queue.Pending.Enqueue(TestSupport.Event(Guid.NewGuid().ToString("D"), attempt: 5));

        await Consumer(queue, TestSupport.Indexer(source, new FakeVectorStore())).PollOnceAsync(CancellationToken.None);

        await Assert.That(queue.Published).IsEmpty();
    }

    private static NewsEventConsumer Consumer(FakeQueue queue, NewsIndexer indexer)
    {
        var options = Microsoft.Extensions.Options.Options.Create(TestSupport.Options());
        var bootstrap = new IndexBootstrap(new FakeVectorStore(), new FakeKeywordStore(), new FakeEmbedder(),
            TestSupport.Sync(new FakeArticleSource(), new FakeVectorStore()), options, NullLogger<IndexBootstrap>.Instance);
        return new NewsEventConsumer(queue, indexer, bootstrap, options, NullLogger<NewsEventConsumer>.Instance);
    }

    /// <summary>Indexer that fails one id, and can hold every call until released.</summary>
    private sealed class FailingIndexer(FakeArticleSource source, FakeVectorStore store, string? failOn, Task? wait = null)
        : NewsIndexer(source, new FakeEmbedder(), store, new FakeKeywordStore(), TestSupport.Pipelines(),
            Microsoft.Extensions.Options.Options.Create(new NewsIdxOptions()), NullLogger<NewsIndexer>.Instance)
    {
        public override async Task<IndexOutcome> IndexAsync(string newsId, CancellationToken cancellationToken)
        {
            if (wait is not null) await wait;
            if (newsId == failOn) throw new InvalidOperationException("embedding failed");
            return await base.IndexAsync(newsId, cancellationToken);
        }
    }
}
