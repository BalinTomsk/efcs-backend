# news_idx — Specification

Version: see `NewsIdx/NewsIdx.csproj` (`<Version>`). History: `../CHANGELOG.md`. Session log: `memory.md`.

## 1. Purpose

Semantic (meaning-based) search over FishFind news articles. An article published or edited in the portal
editor raises a RabbitMQ event; news_idx reads that article from the MySQL `news` table, embeds it with
**BAAI/bge-small-en-v1.5** (in-process, ONNX Runtime, CPU) and stores the vectors in **Qdrant**. The
frontend asks `GET /api/v1/newsidx/search?q=…` and gets articles ranked by meaning, not keywords.

docapi's keyword search (`/api/v1/news/search`, SQL `LIKE`) is untouched; this is an addition.

## 2. Project identity

C# / .NET 10, ASP.NET Core, built and operated like its siblings (`../waterapi`, `../waterservice`):
Polly, Serilog JSON + 7-day file, prometheus-net, `DOTENV_PATH` + `enc:v1:` secrets, self-contained
linux-x64 on `debian:trixie-slim`, uid 10001, public 8080 / private 8081 split by `Connection.LocalPort`.
Libraries: MySqlConnector, Qdrant.Client (gRPC), Microsoft.ML.OnnxRuntime, Microsoft.ML.Tokenizers.

## 3. Layout

```
NewsIdx/
  Program.cs                      host, port split, one-shot --sync / --sync-full
  Configuration/                  options, MySqlUrl (JDBC → MySqlConnector), Polly pipelines, DI
  Domain/                         NewsArticle, NewsEvent (fishfind.news-event.v1), NewsIds
  Data/MySqlNewsArticleSource     the two SELECTs against `news`
  Embedding/BgeSmallEmbedder      tokenizer + ONNX + CLS pooling + L2 norm
  Indexing/                       NewsChunker, NewsIndexer, NewsSyncService, IndexBootstrap
  Messaging/                      RabbitMQ management-API client, NewsEventConsumer
  Search/                         IVectorStore, QdrantVectorStore, NewsSearchService
  Web/                            envelope, exception mapping, health checks, controllers
NewsIdx.Tests/                   TUnit, fakes for MySQL / model / Qdrant / RabbitMQ
scripts/fetch-model.ps1           downloads the model into models/ (git-ignored)
compose.yml                       local Qdrant (+ the service)
```

## 4. Data flow

```
editor (portal) ──publish──▶ RabbitMQ queue fishfind.newsidx.events ──poll──▶ NewsEventConsumer
                                                                            │ news_id
                                     MySQL news (by PK, published only) ◀───┤
                                                                            ▼
                                       NewsChunker → BgeSmallEmbedder → Qdrant (replace article)
frontend ──GET /api/v1/newsidx/search──▶ embed query → Qdrant nearest chunks → one hit per article
```

The event is a notification only. **The database decides**: a published article is (re)indexed;
anything else — draft, unpublished, deleted, unknown — is removed from the index. Every operation is
idempotent, so a duplicate or out-of-order event is harmless.

## 5. Database contract (MySQL `news`, read-only)

news_idx creates **no** database object and needs only `SELECT` (docapi's `portos` account suffices). It
uses docapi's three keys: `MYSQL_NEWS_URL` (JDBC form accepted), `MYSQL_NEWS_USERNAME`, `MYSQL_NEWS_PASSWORD`.

- Article: `SELECT news_id, news_title, news_author, news_source, news_paragraph0..2, news_photo_alt0..2,
  country, news_stamp, lake_id, fish1..3_id, COALESCE(edit_stamp, stamp) FROM news WHERE news_id = @id AND
  news_publish = 1 LIMIT 1`.
- Versions: `SELECT news_id, COALESCE(edit_stamp, stamp) FROM news WHERE news_publish = 1`.
- **BLOB hazard:** never name `news_photo0..2` in any query; never put LONGTEXT in a multi-row query. Text is
  read only by primary key, one row at a time.
- News ids are normalized to lower-case GUID form everywhere (MySQL compares case-insensitively, Qdrant does not).
- `news_stamp`/`stamp`/`edit_stamp` are treated as UTC.

## 6. Embedding

- Model: `bge-small-en-v1.5`, 384 dimensions, `models/bge-small-en-v1.5/{model.onnx,vocab.txt}`.
- Tokenizer: BERT WordPiece, lower-cased, `[CLS] … [SEP]`, truncated at 512 tokens.
- Pooling: `[CLS]` hidden state, L2-normalized (cosine = dot product).
- Queries are prefixed `Represent this sentence for searching relevant passages: `; passages are not.
- `Threads` = 1 by default (small shared droplets). Batches of 8 chunks per ONNX call.
- Chunking: title + paragraphs + photo captions, tags stripped, 220-word windows with 40-word overlap;
  every chunk starts with the title.

## 7. Qdrant contract

Collection `news_articles` (`NewsIdx:Qdrant:Collection`), vector 384 cosine, created at startup if
missing; startup refuses a collection of another size. One point per chunk:

| field | type | index |
|---|---|---|
| id | UUID = v5-style hash of `"{news_id}:{chunk}"` | — |
| `news_id` | keyword | yes |
| `chunk` | integer | yes |
| `country` | keyword (`""` when none) | yes |
| `stamp_unix` | integer | yes |
| `title`, `source`, `stamp` (yyyy-MM-dd), `lake_id`, `text` (≤ 2000 chars), `model` | — | — |
| `last_edit_unix` | integer — the sync's change marker | — |

Re-index = upsert chunks 0..n-1 (fixed ids overwrite) then delete `news_id = X AND chunk ≥ n`, so an article
is never missing from search while it is re-indexed.

## 8. Events — `fishfind.news-event.v1` (the frontend's half is NOT built yet)

Transport: RabbitMQ **management HTTP API**, like the account events — publish
`POST /api/exchanges/%2F/amq.default/publish`, routing key = queue name; news_idx polls
`POST /api/queues/%2F/fishfind.newsidx.events/get` (`ack_requeue_false`). Separate queue from cproxy's
`fishfind.account.events` — the two consumers must never share one.

```json
{ "schema": "fishfind.news-event.v1", "eventId": "<guid>", "eventType": "fishfind.news.article",
  "action": "created|updated|published|unpublished|deleted", "aggregateId": "<news_id>",
  "occurredUtc": "2026-10-14T12:00:00.0000000Z", "source": "fishfind-frontend" }
```

Same `BasePayload` shape as `AccountEventPublisher`, with persistent delivery (`delivery_mode: 2`). The
publisher should send it after every successful write path: `sp_news_admin_draft_publish`,
`sp_news_admin_photo_update`, `sp_news_doc_insert`, `sp_news_doc_update`, and any delete/unpublish.

Consumer rules: unusable message → logged, dropped. Failure → re-published with `"attempt": n+1` and the
poll backs off (2 s doubling to 60 s); dropped after `MaxAttempts` (5). Duplicates for one article in a batch
are indexed once. A dropped or lost event is repaired by the periodic sync (§9).

The consumer runs only when `RABBITMQ_MANAGEMENT_URL`, `RABBITMQ_USERNAME` and `RABBITMQ_PASSWORD` are all set.

## 9. Initial load and sync ("index every article in MySQL")

`NewsSyncService` compares MySQL's published versions with the index's chunk-0 `last_edit_unix`:

- **Incremental:** index missing or changed articles only (second precision).
- **Full:** re-embed every published article (needed after a model/chunking change).
- Both delete indexed articles that are no longer published — **except** when MySQL returns zero rows while
  the index is non-empty (prune skipped, warning): an empty answer is far likelier to be a fault.
- Single-flight; one failing article is counted and the run continues.

Triggers:

| how | what |
|---|---|
| startup, `NewsIdx:Sync:OnStartup` | `IfEmpty` (default) → full run when the collection has no points; `Always` → incremental; `Never` |
| every `NewsIdx:Sync:IntervalMinutes` (60; 0 = off) | incremental |
| `POST /admin/sync?full=true` (8081) | full, in the background; `GET /admin/sync` = progress; 409 if running |
| `NewsIdx --sync-full` / `--sync` | one-shot: run, exit 0 (all ok) / 2 (some failed) / 1 (fatal) |
| `POST /admin/index/{newsId}` (8081) | one article, synchronously |

## 10. HTTP API (port 8080)

### `GET /api/v1/newsidx/search?q={text}[&limit=10][&country=CA]`

- `q` required, ≤ 256 chars; `limit` 1..50 (default 10); `country` optional two-letter code.
- Fetches `limit × 4` nearest chunks, keeps each article's best chunk, returns best first.

```json
{ "data": { "query": "walleye limits", "country": null, "count": 1, "items": [
    { "newsId": "…", "title": "…", "source": "…", "country": "CA", "date": "2026-09-01",
      "score": 0.8123, "snippet": "…", "lakeId": null } ] },
  "error": null, "meta": { "timestamp": "…" } }
```

Errors (envelope `error.code`): 400 `invalid_request`; 503 `unavailable` (Qdrant breaker open, `Retry-After: 30`);
404 `not_found`; 500 `internal_error` (details only in the log).

### `GET /health` — `{ status, version, uptime }` (Docker HEALTHCHECK target)

## 11. Management (port 8081 — never publish)

`/health/live` (process only), `/health/ready` (Qdrant unhealthy = not ready; MySQL failing = degraded,
search still served), `/metrics`, `/admin/*` (§9). Metrics: `newsidx_index_articles_total{outcome}`,
`newsidx_index_chunks_total`, `newsidx_sync_runs_total{mode,outcome}`, `newsidx_events_total{outcome}`,
`newsidx_search_seconds`.

## 12. Deployment

- **CTRL only (owner rule, 2026-10-16).** news_idx runs on the host named CTRL and nowhere else.
- **Placement (2026-10-14):** a dedicated Windows Server host running Docker under Rancher Desktop, not
  a droplet. news_idx runs with `--memory 2g` and 6 ONNX threads (~740 MiB while loading); Qdrant with
  `--memory 512m` (~70 MiB for 4.9k articles). Steps and host details: git-ignored `do-update.md`.
- **MySQL reachability.** Winhost MySQL is reachable from that host and from docapi's droplet, not from the workstation.
- **Gateway.** Expose only through cproxy: a new upstream for `/api/v1/newsidx/*`, like waterapi's
  `/api/v1/water/*`. Never publish 8080 or 8081 to the internet. Qdrant binds to localhost / the Docker network only.
- **Frontend.** Two changes: a `NewsEventPublisher` (§8) and a search UI calling the gateway.
- Model: fetched 2026-10-14 from revision `main` — `model.onnx` 126.9 MB
  `sha256=828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35`, `vocab.txt`
  `sha256=07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3`. Before the first image, rebuild
  with `-ExpectModelSha256 828e1496…cf35` so the shipped file is provably this one.

## 13. Build, test, run

```
dotnet build NewsIdx.slnx
dotnet run --project NewsIdx.Tests
docker compose up -d qdrant ; ./scripts/fetch-model.ps1 ; dotnet run --project NewsIdx --no-launch-profile
```
