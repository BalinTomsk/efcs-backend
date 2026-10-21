# news_search — Specification

Version: see `NewsSearch/NewsSearch.csproj`. History: `../CHANGELOG.md`. Session log: `memory.md` (local).

## 1. Purpose

The portal's news search asks several engines in parallel and shows each engine's results as soon as
they arrive. news_search is the home for every engine **except** the SQL keyword search:

| source | where | status |
|---|---|---|
| `sql` (LIKE) | docapi `/api/v1/news/search` on **jnode** | live (unchanged) |
| `vector` | news_search → news_idx on **CTRL** (`/newsidx/search`) | 0.1.0 |
| `bm25` | news_search → news_idx on **CTRL** (`/newsidx/bm25`) | 0.2.0 |
| `image` | news_search | planned |

The SQL search stays on jnode and everything else is on csnode, so if one node fails, the other's
results still show. news_search never calls docapi.

## 2. Project identity

C# / .NET 10, built like `../waterapi` (same host skeleton, `{data,error,meta}` envelope, 8080/8081 split by
`Connection.LocalPort`, Serilog JSON, prometheus-net, self-contained linux-x64 on `debian:trixie-slim`, uid
10001). No database and no model. Workstation GC, because csnode is ~1 GB shared by four .NET services.

## 3. HTTP API (port 8080)

### `GET /api/v1/newssearch/sources`
`data`: `[{ name, path, description }]`, the sources this instance answers. Only configured sources are listed.

### `GET /api/v1/newssearch/{source}?q={text}[&limit=10][&country=CA]`
- `q` required, ≤ 256 chars; `limit` 1..50 (default 10); `country` optional two letters.
- `data`: `{ source, query, country, count, tookMs, items:[{ newsId, title, source, country, date, score, snippet, lakeId, relevance, missing }] }`.
  The items have the same shape for every source, so the page can merge them. `score` is only comparable within one source.
  `relevance` (0..100) is the score as a percent (vector: cosine x 100; bm25: share of the query's best hit), and the
  page sorts all sources by it. `missing` lists the query words the article does not contain. Both come from news_idx
  0.6.0+ and are null with an older one.
- Errors: 400 `invalid_request`; 404 `not_found` (unknown source); **503 `unavailable`** + `Retry-After: 30`
  (the source's backend is down, slow or behind an open breaker; the page then shows the other sources).

### `GET /health` — `{ status, version, uptime }`

Management (8081, never published): `/health/live`, `/health/ready`, `/metrics`
(`newssearch_requests_total{source,outcome}`, `newssearch_source_seconds{source}`).

## 4. Sources

`ISearchSource` (`Name`, `Description`, `SearchAsync`). A new source is one class plus one registration,
with no change to cproxy or the controller.

### vector, bm25 (`NewsIdxSource`)
`vector` calls news_idx `GET /api/v1/newsidx/search` and `bm25` calls `GET /api/v1/newsidx/bm25`, both at
`NEWSIDX_URL` (CTRL's forwarded port), through one shared HTTP client. Resilience: a 5 s
total budget (`NewsSearch:Vector:TimeoutSeconds`), one retry after 200 ms, a per-attempt timeout of 60 %
of the budget, and a circuit breaker (50 % failures over 30 s, minimum 5 calls, open 30 s). CTRL's
firewall must allow csnode's public address (rule `news_idx 8080 - block all but cproxy and LAN`, see
news_idx's runbook).

## 5. Configuration

| key | meaning |
|---|---|
| `NEWSIDX_URL` | news_idx origin, e.g. `http://vector.fishfind.info:4324`. Unset = no `vector` and no `bm25` source |
| `NewsSearch__Search__MaxLimit` etc. | limits, see `appsettings.json` |

## 6. Exposure

Only through cproxy (0.25.0+, `CPROXY_NEWSSEARCH_UPSTREAM`, prefix `/api/v1/newssearch/`), over the csnode ↔
cproxy VPC peering, like waterapi. Host port **8091** on csnode (8080 water, 8081 weather, 8090 waterapi),
bound to localhost and the VPC address only.

## 7. Frontend

`News.aspx` (registered users) still renders the SQL results server-side from docapi. After the page
loads, the browser calls `NewsSearchSource.ashx?src={bm25|vector}&q=` (server-side, through cproxy) once per
engine. Their hits fill a "More results" panel, minus articles already in the SQL list or shown by an
earlier engine. Each source is its
own call, so a slow or failed source never delays or blanks the others.

## 7a. Build, test, run

```
dotnet build NewsSearch.slnx
dotnet run --project NewsSearch.Tests
NEWSIDX_URL=http://localhost:8080 dotnet run --project NewsSearch --no-launch-profile
```

## Service status (admin page)

`Status/ServiceStatusReporter.cs` publishes `fishfind.service-status.v1` to the RabbitMQ queue
`fishfind.servicestatus.events` (management HTTP API, `POST /api/exchanges/%2F/amq.default/publish`, non-persistent):
`start` at startup, `heartbeat` every `STATUS_INTERVAL_SECONDS` (60, minimum 10) with the latest `ServiceActivity`,
`stop` on a clean shutdown (3 s budget). Service name `news_search`; machine = `STATUS_MACHINE`, else the machine name
(lower-cased). `SearchService` records `search <source>` per request — the engine only, never the query text. Registered by `AddServiceStatusReporting` only when `RABBITMQ_MANAGEMENT_URL`,
`RABBITMQ_USERNAME` and `RABBITMQ_PASSWORD` are all set; `ServiceActivity` is always registered and optional for its
callers. A failed publish logs one warning per outage and is never retried. The contract and the consumer
(efcs-backend `adminservice` → `dbo.ServiceStatus` → fishfind-frontend `stats/Default.aspx`) are specified in
`../../adminservice/docs/specification.md`.
