# Changelog

All notable changes to `news_find`. Group entries under `Added` / `Changed` / `Fixed` / `Removed` /
`Security`, and say **why**, not only what. The specification describes the current state; this file
carries the history.

## [Unreleased]

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
