# Changelog

All notable changes to `waterapi`. Group entries under `Added` / `Changed` / `Fixed` / `Removed` /
`Security`, and say **why**, not only what. The (local) specification describes the current state; this
file carries the history.

## [Unreleased]

## [0.1.1] — 2026-09-24 (DEPLOYED on csnode, behind cproxy 0.18.0)

### Fixed
- **Every request through the gateway was a 404.** The public/management split used
  `RequireHost("*:8080")`/`("*:8081")`, which matches the **Host header**, not the port the connection
  arrived on. Behind Docker's `8090→8080` mapping, callers send `Host: <vpc-address>:8090`, so no endpoint
  matched. A middleware now splits the surfaces by `Connection.LocalPort`: `/health/live`, `/health/ready`
  and `/metrics` only on 8081, everything else only on the public port. New test
  `PublicApi_AnswersWhateverHostPortTheCallerUsed`; it failed before the fix. The 0.1.0 end-to-end test
  had called port 8080 directly, which is why it missed this.

### Deployment notes
- The first 0.1.0 deploy **hung csnode**: 464 MB of RAM, no swap, and two other .NET services. The user
  resized it to about 1 GB. waterapi now runs with `--memory 256m --memory-swap 256m --cpus 0.5` and
  uses about 70 MiB after the cache load.

### Changed
- Routes moved from `/api/v1/station/*` to **`/api/v1/water/station/*`**. waterapi is exposed through
  the cproxy gateway (0.18.0), which sends `/api/v1/water/*` here. docapi already owns
  `/api/v1/station/{id}`, so a shared path would have been ambiguous at the gateway. Nothing had
  consumed the old paths; 0.1.0 was never deployed.

## [0.1.0] — 2026-09-23 (not deployed)

### Added
- New service, organised like docapi and built like the C# siblings on the droplet (waterservice,
  weather), so the frontend can get water-station data from an API instead of querying SQL Server itself.
- **Station controller.** `GET /api/v1/water/station/map?country=&state=` returns every `dbo.vMapView` pin for
  a country (the data `Editor/ViewMap.aspx.cs` reads directly today), and `GET /api/v1/water/station/{sid}`
  returns one. Answers come from an in-process cache, never a per-request query: a background refresher
  loads CA and US at startup and every 15 minutes, a failed refresh keeps the last good snapshot, and
  concurrent cold requests share one query. Responses carry an ETag (`If-None-Match` ⇒ 304) and
  `Cache-Control`, and are Brotli/Gzip-compressed, because the US map is ~1 MB of JSON (~281 KB brotli).
- `state` is validated as two letters and filtered in memory. The legacy page concatenates it into its
  SQL text.
- `/health` on 8080, `/health/live`, `/health/ready` (DB check when SQL-backed) and `/metrics` on private
  8081; Prometheus gauges and counters for cache size, load time and refresh outcome.
- No-DB mode: with `DB_URL` unset the service starts on an in-memory backing, as docapi does under its
  default profile.

### Fixed (before release)
- `Countries` bound to `CA,US,CA,US`, so each refresh loaded both countries twice. The configuration
  binder appends to an array property that already has elements. The options default is now empty and
  the cache de-duplicates the list. Caught in a local run against the real view.
