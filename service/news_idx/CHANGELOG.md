# Changelog

All notable changes to `news_find`. Group entries under `Added` / `Changed` / `Fixed` / `Removed` /
`Security`, and say **why**, not only what. The specification describes the current state; this file
carries the history.

## [Unreleased]

## [0.3.0] — 2026-10-16

### Added
- **BM25 keyword search**, `GET /api/v1/newsidx/bm25` (same parameters and answer shape as `/search`). Semantic
  search drops rare exact terms (a weak "keta" hit falls under MinScore), and SQL `LIKE` does not rank at all.
  BM25 finds the exact words and ranks rare ones higher.
- Second Qdrant collection `news_articles_bm25` with one point per article and a sparse vector `bm25` with
  `modifier = idf`. Qdrant computes the IDF from its live counts, and news_idx stores only the saturated,
  length-normalized term frequency (k1 1.2, b 0.75, fixed avgdl 256, title words x2). So an edit never
  re-weights other articles. It is a separate collection because Qdrant cannot add a vector to the live
  dense collection.
- Analyzer `bm25-porter-v1`: accent folding, lower case, English stop words, Porter stemmer, FNV-1a term
  hashing. Changing it needs a full sync.
- The sync and every event now keep both indexes in step. An article missing from either one is re-indexed.
  At startup, an empty BM25 collection next to a filled vector one triggers an incremental sync, which
  fills it. This re-embeds every article once.
- `QdrantKeywordStoreTests` runs against a real Qdrant when `QDRANT_TEST_URL` is set. It caught a
  `Condition -> Filter` null conversion that the fakes could not.

## [0.2.0] — 2026-10-16

### Changed
- **Renamed news_find → news_idx** everywhere: folder `service/news_idx`, project `NewsIdx`, config section
  `NewsIdx` (`NewsIdx__…` env overrides), route `/api/v1/newsidx/search`, queue `fishfind.newsidx.events`,
  metrics `newsidx_*`, container and image `news_idx`. This was the owner's naming decision. It is breaking:
  cproxy needs 0.24.0 (`CPROXY_NEWSIDX_*`), and the frontend publisher needs the new queue name. The Qdrant
  collection (`news_articles`) and the event schema (`fishfind.news-event.v1`) are unchanged, so nothing is
  re-indexed.
- news_idx runs **on CTRL only**. This is a documented rule, not a code check.

## [0.1.0] — 2026-10-14 (DEPLOYED on the vector host)

### Fixed (before first release)
- **The first full sync failed on every article** with `InvalidCastException: Guid → String`.
  MySqlConnector maps `CHAR(36)` columns to `Guid` by default, and `news_id`, `lake_id` and `fish*_id` are
  all `CHAR(36)`. The connection string now sets `GuidFormat=None`. The unit test pins it; the fakes could
  not catch this.

### Added
- Semantic search over published news: `GET /api/v1/newsfind/search`. Keyword search (docapi's SQL `LIKE`)
  misses articles that say the same thing in other words; embeddings find them.
- bge-small-en-v1.5 runs in-process on ONNX Runtime (CPU, 1 thread by default): no embedding service to
  operate and no article text leaves the host.
- RabbitMQ consumer for `fishfind.news-event.v1` on `fishfind.news.events`, over the management HTTP API —
  the transport the frontend and cproxy already use, so no AMQP port has to be opened. Events carry only
  the article id; the article is always read from MySQL, so the index follows the database, not the message.
- Initial load and repair: `NewsFind --sync-full`, `POST /admin/sync?full=true`, automatic full load into an
  empty collection, and an hourly incremental sync that catches lost events and control-panel edits.
- Wipe guard: an empty MySQL answer never prunes a non-empty index.
