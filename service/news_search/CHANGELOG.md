# Changelog

All notable changes to `news_search`. Say **why**, not only what.

## [Unreleased]

## [0.3.0] — 2026-10-16 (not deployed)

### Added
- **Service status for the admin page.** `Status/ServiceStatusReporter` publishes `fishfind.service-status.v1`
  (`start`, a `heartbeat` every 60 s, `stop` on a clean shutdown) to `fishfind.servicestatus.events` over the
  RabbitMQ management HTTP API; efcs-backend `adminservice` stores it in `dbo.ServiceStatus` and
  fishfind-frontend `stats/Default.aspx` shows it. The heartbeat carries the last engine searched (`search vector`), never the query text. Runs only when `RABBITMQ_MANAGEMENT_URL`,
  `RABBITMQ_USERNAME` and `RABBITMQ_PASSWORD` are set; `STATUS_MACHINE` names the host (in Docker the machine
  name is the container id). Best-effort: a broker outage is one warning, never a retry loop or a slower start.

## [0.2.0] — 2026-10-16

### Added
- `bm25` source, `GET /api/v1/newssearch/bm25`, which calls news_idx `/api/v1/newsidx/bm25` (needs news_idx
  0.3.0). It finds exact words and names that the vector source scores too low. It shares the news_idx HTTP
  client, so it also shares the 5 s budget, the retry and the breaker: news_idx down = both sources 503.
  `VectorSource` and `Bm25Source` are now two small subclasses of `NewsIdxSource`.

## [0.1.0] — 2026-10-16

### Added
- `GET /api/v1/newssearch/sources` and `GET /api/v1/newssearch/{source}`, one endpoint per engine. The page
  asks every engine in parallel and shows each result set as soon as it arrives, so one slow engine never
  delays the others.
- `vector` source: semantic search through news_idx on CTRL, with a 5 s budget, one retry and a circuit
  breaker. An outage answers 503 on that source only.
- SQL LIKE search deliberately stays on docapi (jnode) so that either node can fail without blanking the
  other's results.
