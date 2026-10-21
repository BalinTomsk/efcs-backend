# Changelog

All notable changes to `news_find`. Group entries under `Added` / `Changed` / `Fixed` / `Removed` /
`Security`, and say **why**, not only what. The specification describes the current state; this file
carries the history.

## [Unreleased]

## [0.6.0] — 2026-10-20

### Added
- **`relevance` (0..100) on every hit of `/search` and `/bm25`**, so the page can show a percent and sort both
  engines' hits into one list. Vector: the cosine similarity x 100. BM25: the score as a share of the query's best
  BM25 hit (best = 100). BM25 scores have no fixed ceiling, so a percent of an absolute maximum would mean nothing.
- **`missing`: the query words an article does not contain**, like Google's "Missing: ...". It is read from the
  article's stored BM25 terms (`IKeywordStore.GetTermsAsync`, one Qdrant retrieve per search), so it covers the whole
  article and works for both engines. Stop words are never reported. `[]` = every word found. `null` = unknown: the
  article is not in the BM25 index, or the lookup failed. A failed lookup is logged and never fails the search.

## [0.5.0] — 2026-10-20

### Added
- **Runs as a Windows service** (`Microsoft.Extensions.Hosting.WindowsServices`, `UseWindowsService`, service name
  `news_idx`). On 2026-10-20 CTRL rebooted twice, and search stayed down until someone signed in, because Rancher
  Desktop is a per-user app. A service starts at boot with nobody signed in. It is a no-op in Docker and under
  `dotnet run`. Plan and cutover steps: `docs/windows-services-plan.md`.
- **`NewsIdx__ManagementUrl`** (default `http://0.0.0.0:8081`) narrows the management listener's host. In Docker,
  8081 is never published, but on bare Windows it would sit on the LAN, so CTRL sets `http://127.0.0.1:8081`. Only
  the host can change: any other port, scheme or path is refused at startup, because the public/management split
  is made by the port a connection arrived on.

### Changed
- As a Windows service, the process first moves to its own folder (`AppContext.BaseDirectory`). A service starts in
  `C:\Windows\System32`, and `logs/`, `models/` and a relative `DOTENV_PATH` mean "next to the executable", as
  in the container's `/app`.

## [0.4.1] — 2026-10-20

### Added
- **Qdrant on the admin status page.** `Status/QdrantStatusReporter` reads Qdrant's `/telemetry` every heartbeat
  and publishes service `qdrant` (Qdrant's own version and start time) on news_idx's machine. Qdrant cannot
  publish itself, and on 2026-10-20 it was down for an hour after a CTRL reboot without the page showing it.
  Nothing is sent while Qdrant is down, so the page marks it dead. REST endpoint: `QDRANT_REST_URL`, else the
  gRPC URL with port 6333.

### Changed
- **`STATUS_INTERVAL_SECONDS` (env file): heartbeat period, minimum 60 s** (was a 10 s floor). Lower values are raised
  to 60, so a service never reports to RabbitMQ more often than once a minute; larger values cut traffic further (then
  raise the page's `ServiceStatusDeadAfterSeconds` to at least 3x). Default unchanged: 60.

## [0.4.0] — 2026-10-16 (not deployed)

### Added
- **Service status for the admin page.** `Status/ServiceStatusReporter` publishes `fishfind.service-status.v1`
  (`start`, a `heartbeat` every 60 s, `stop` on a clean shutdown) to `fishfind.servicestatus.events` over the
  RabbitMQ management HTTP API; efcs-backend `adminservice` stores it in `dbo.ServiceStatus` and
  fishfind-frontend `stats/Default.aspx` shows it. The heartbeat carries the last event indexed (`upsert article <id>`). Not in a `--sync` one-shot run. Runs only when `RABBITMQ_MANAGEMENT_URL`,
  `RABBITMQ_USERNAME` and `RABBITMQ_PASSWORD` are set; `STATUS_MACHINE` names the host (in Docker the machine
  name is the container id). Best-effort: a broker outage is one warning, never a retry loop or a slower start.

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
