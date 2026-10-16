# Changelog

All notable changes to `news_search`. Say **why**, not only what.

## [Unreleased]

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
