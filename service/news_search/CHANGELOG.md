# Changelog

All notable changes to `news_search`. Say **why**, not only what.

## [Unreleased]

## [0.1.0] — 2026-10-16

### Added
- `GET /api/v1/newssearch/sources` and `GET /api/v1/newssearch/{source}`, one endpoint per engine. The page
  asks every engine in parallel and shows each result set as soon as it arrives, so one slow engine never
  delays the others.
- `vector` source: semantic search through news_idx on CTRL, with a 5 s budget, one retry and a circuit
  breaker. An outage answers 503 on that source only.
- SQL LIKE search deliberately stays on docapi (jnode) so that either node can fail without blanking the
  other's results.
