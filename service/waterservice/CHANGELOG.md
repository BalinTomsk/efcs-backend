# Changelog — `water-station-pusher` (C# / .NET 10)

All notable changes to this service go **in this file**. See [Changelog rules](#changelog-rules) at the
bottom before adding an entry.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The version stamp lives in
`WaterService/WaterService.csproj` (`<Version>`) and is surfaced by `/health`; keep the two in step.

---

## [Unreleased]

## [10.4.1] — 2026-10-17 (not deployed)

### Changed
- **`DOTNET_TieredPGO=0` in the image** (was 1). csnode is a 1 GB droplet running five .NET services with about
  125 MB available; PGO keeps extra instrumented code versions. Measured on waterapi with 1 CPU: about 15 MB less per
  container. Not a leak fix -- csnode memory was flat over a 4-minute per-process sample; it is simply full.

## [10.4.0] — 2026-10-16 (not deployed)

### Added
- **Service status for the admin page.** `Status/ServiceStatusReporter` publishes `fishfind.service-status.v1`
  (`start`, a `heartbeat` every 60 s, `stop` on a clean shutdown) to `fishfind.servicestatus.events` over the
  RabbitMQ management HTTP API; efcs-backend `adminservice` stores it in `dbo.ServiceStatus` and
  fishfind-frontend `stats/Default.aspx` shows it. The heartbeat carries the last station processed (`station 05BH004`). Web host only, not `--console`. Runs only when `RABBITMQ_MANAGEMENT_URL`,
  `RABBITMQ_USERNAME` and `RABBITMQ_PASSWORD` are set; `STATUS_MACHINE` names the host (in Docker the machine
  name is the container id). Best-effort: a broker outage is one warning, never a retry loop or a slower start.

---

## [10.3.1] — 2026-10-16

### Fixed

- **False "cycle overran" warnings.** After 10.3.0, US cycles of ~2,700 s (20:46 and 23:44 UTC on
  2026-10-15) were logged as overruns and counted on `water_cycle_overrun_total`. `Task.Delay` sometimes
  wakes a few ms before the cron fire, so the cycle "started" at 19:59:59.99x and its own 20:00 fire was taken
  as the next one. The check now measures from the fire the loop waited for. The loop also computes the
  next fire from the later of now and the last fire, so a cycle shorter than that early wake can no longer
  run twice for the same fire.

### Changed

- **`PauseBetweenStationsMs` 1000 -> 500** in `appsettings.json`. At 1000 the CA pass took 66–72 min
  (3,981–4,351 s measured 2026-10-15), so it overran the hour and refreshed only every 2 h. The pause is
  ~37 min of that; 500 ms takes off ~18 min, bringing the pass back under the hour. The datamart serves
  static files.

## [10.3.0] — 2026-10-15

### Fixed

- **The "hourly" cycle took ~28 hours** (`Station cycle duration` 100,895-108,236 s on 10.1.1), so CA data
  refreshed about once a day and, because the single scheduler loop awaited both passes, so did US data
  even after 10.2.0 cut the US pass to ~70 min. Measured on the droplet on 2026-10-11: ~70 CA stations an
  hour (~51 s each) with almost no failures, skips or breaker trips; the Environment Canada CSV downloads
  in ~0.13 s. The time went to the database: each CA file carries ~600 five-minute readings and
  `SaveStationDataAsync` sent one `EXEC dbo.sp_UpdateWaterData` per reading to a server ~75 ms away
  (600 x 75 ms = 45 s) plus the 5 s pause. The Java twin already batched this write, which is why its CA
  pass takes ~70 min.
  - CA readings are now written as parameterised `EXEC` batches of up to 500 per command (one or two round
    trips per station). US series likewise go in one batch per station. No database change; validated
    against a real SQL Server (insert, update, NULLs, two chunks, rollback on a failing call).
  - The CA and US passes now run on **independent schedules** of the same cron, so a slow pass delays only
    its own next run. Each scheduled pass post-processes after itself (serialised; the procs are
    idempotent). Startup runs and console mode still run both countries together and post-process once.
  - `Station cycle duration` / `Station cycle overran` and the scheduled `Station cycle completed` lines now
    carry `country`; all stay at `Information` (overrun at `Warning`) per the log-level policy.

### Changed

- **`PauseBetweenStationsMs` back to 1000 ms** in `appsettings.json` (5000 since 10.0.3, with no recorded
  reason; the code default and the Java service use 1000). The datamart serves static files, and at 5 s
  the pause alone added ~3 h to a 2,219-station pass.

---

## [10.2.0] — 2026-10-11

**Deploy prerequisite:** `USGS_API_KEY` (encrypted, `enc:v1:`) in the droplet env file.

### Changed

- **User-Agent renamed to `UofW-water service-USGC`** (was `water-station-envfish/10.0.3` in
  `appsettings.json`, `water-station-pusher/10.0.3 (+https://fishfind.info)` as the code default). It is
  sent to both Environment Canada and USGS. The Java service keeps `water-station-pusher` and gets its
  own USGS key later, so the two implementations can now be told apart in USGS's traffic.

- **US data now comes from the USGS Water Data API, not the legacy WaterServices `nwis/iv` feed.** USGS
  wrote to us on 2026-10-10 about traffic from the `water-station-pusher` user agent: WaterServices is
  slowed from **2026-11-16**, has planned outages from **2027-01-27** and is switched off on
  **2027-02-22**. We were calling it once per station, about 9,500 calls an hour from each of the two
  redundant services. The new API (`api.waterdata.usgs.gov/ogcapi/v1`) is rate-limited per key
  (1,000 requests/hour), so a per-station port was not an option.

  Each US pass now pages the whole `latest-continuous` collection once (~2-3 requests of up to 50,000
  rows) and each station is a lookup in that snapshot (`UsgsWaterDataClient` replaces `XmlFetcherUS`;
  `StationProcessorUS.PrepareCycleAsync` is called by `StationWorker` before the US loop). The US pass no
  longer pauses between stations, since there is no per-station request left to be polite about.

  **Nothing changes in the database.** `UsgsParameterMap` maps each USGS parameter code to the exact
  name/unit pair the WaterML parser used to send to `dbo.sp_push_us_water_data` (harvested from live legacy
  responses), and readings are still dated by the station's **local** day: site time zones are looked up
  once per process from `monitoring-locations`. Verified against the local DB on 2026-10-11: station
  `08313000` stored the same converted discharge/elevation as before, and the snapshot covers 8,518 of our
  US stations with data from the last 3 days against 8,514 delivered by the legacy feed.

  Trade-off: `latest-continuous` holds only the newest value per series, so the 3-day backfill the
  legacy `period=P3D` gave is gone. Hourly polling and the redundant Java service make a gap unlikely.

### Added

- **`USGS_API_KEY`** secret (environment / `.env`, `enc:v1:` supported), sent as `X-Api-Key`. Without it
  the API's anonymous limit applies. **Deploy prerequisite:** add it to the droplet env file.
- `Water:Worker:UsgsBaseUrl` (default `https://api.waterdata.usgs.gov/ogcapi/v1/collections`).

### Removed

- `XmlFetcherUS` and the WaterML parser (and their tests).

---

## [10.1.1] — 2026-08-06

Metric-correctness patch over `10.1.0`. No deployment changes — the master key mount introduced by `10.1.0`
is still required, nothing new is.

### Fixed

- **`water_station_processed_total` no longer counts skipped stations as failures.** ⚠️ **Breaking change to
  the metric's meaning — see the dashboard/alert note below.** `StationWorker` collapsed every non-`Processed`
  outcome into a bool, so `ProcessingOutcome.Skipped` — which only means the upstream feed publishes no data
  for that station (HTTP 404), the normal case for most of the CA fleet — was recorded as
  `outcome="failure"`. Observed on 2026-08-06 during the 00:00 UTC cycle (release `10.1.0`): CA reported
  **2 success / 44 "failure"** while the logs contained **zero** "CA station processing failed" warnings —
  all 44 were skips. The metric read as a near-total CA outage on a healthy service, and any genuine failure
  was buried in that noise.

  `WaterMetrics.StationProcessed` now takes the full `ProcessingOutcome` instead of a `bool` and emits a
  distinct label per case:

  | `outcome` | `ProcessingOutcome` | Was |
  |---|---|---|
  | `success` | `Processed` | `success` |
  | `skipped` | `Skipped` | `failure` |
  | `failure_503` | `FailedHttp503` | `failure` |
  | `upstream_open` | `FailedUpstreamOpen` | `failure` |
  | `failure` | `Failed` | `failure` |

  Nothing else changed: the pass still counts only `Processed` as a success, and 503 backoff and
  open-breaker pass-stopping behave exactly as before. The mapping lives in `WaterMetrics.OutcomeLabel`, and
  `StationWorkerTests.RunOnce_SkippedStation_IsNotCountedAsAFailure` /
  `RunOnce_EachFailureKindGetsItsOwnOutcomeLabel` pin it.

  **Action required — Grafana panels and alerts using `outcome="failure"` must be updated.** The
  `failure` series drops sharply (most of its volume was skips) and three new series appear. Rules written
  as `outcome!="success"` are now *worse* than before — they match `skipped` and will fire on healthy
  cycles. Use `outcome=~"failure|failure_503"` for "something is actually broken", and treat `skipped` and
  `upstream_open` as informational. Because the old and new series share a metric name, panels spanning the
  upgrade will show a discontinuity rather than a clean cut-over.

---

## [10.1.0] — 2026-08-06

Java→C# parity release: brings over the three Java `service/waterservice` commits made after the 503-backoff
sync, adds `StationWorker` test coverage, and encrypts the droplet's environment file.

**Deployment note:** this release is the first to require the **master key mount** on the droplet, because
`waterservice.env` was encrypted as part of it — see `docs/do-update.md` Step 5/8. Minor bump rather than a
patch because `enc:v1:` support is a new capability.

### Added

- **`enc:v1:` encrypted configuration values** (`Configuration/SecretCodec.cs`). `DB_URL` / `DB_USERNAME` /
  `DB_PASSWORD` may be stored as AES-256-GCM ciphertext (12-byte nonce, 128-bit tag, base64url) instead of
  plaintext. Wire-compatible with the Java services and `efj-backend/secret/Protect-Env.ps1`, so one
  encrypted `.env` serves both. Decryption covers **both** delivery paths: the `DOTENV_PATH` file *and*
  values already injected as real environment variables (how Docker's `--env-file` delivers them). The
  variable name is bound in as additional authenticated data, so a ciphertext cannot be relocated between
  keys. Unmarked values pass through verbatim, so a plaintext or partially-encrypted file stays valid and
  this image runs against the existing all-plaintext `.env`. A missing or wrong key is a **hard startup
  failure**, never a silent pass-through. Key material comes from `FF_MASTER_KEY_FILE` (a path, preferred)
  or `FF_MASTER_KEY`, 32 bytes as hex or base64.
  *Deploy note:* if the env file is ever encrypted, the container needs the key mount — see
  `docs/do-update.md` Step 8.
- **`StationWorkerTests`** — 24 tests covering cycle/pass semantics, HTTP 503 backoff, open-breaker pass
  stopping, post-processing failure ordering, cycle-overrun accounting, the three startup modes, and the
  startup log-level policy below. To make this possible without new interfaces or DI indirection,
  `WaterStationRepository`, `StationPostProcessingService`, `StationProcessorCA`/`US` and `WaterMetrics`
  are now **unsealed** with the members `StationWorker` calls marked `virtual`. **Do not re-seal them.**
- **`XmlFetcherUSTests`** and **`SecretCodecTests`** (the latter pins cross-language interop against
  fixtures generated by `Protect-Env.ps1`).

### Changed

- **Open upstream circuit breaker now stops the country pass.** Polly's `BrokenCircuitException` maps to a
  new `ProcessingOutcome.FailedUpstreamOpen`, and `StationWorker` abandons that country's pass instead of
  marching through thousands of stations logging identical failures. The other country's pass is
  unaffected, and the next cycle retries once the breaker has had time to close. An open breaker does
  **not** record a per-station HTTP 503 backoff — the station did nothing wrong.
- **USGS response-body I/O failures now name the station.** A body that dies mid-read (premature EOF,
  chunked-encoding error) was flattened by `HttpContent` into a bare
  `HttpRequestException("Error while copying content to a stream.")` — no station, no cause,
  indistinguishable from every other station in the log. `XmlFetcherUS` now rethrows it with the station
  and state named, still as an `HttpRequestException` so it is retried exactly as before. `IOException`
  was also added to the feed pipelines' transient predicate.
- **HTTP 503 backoff reset demoted to `Debug`.** "Reset station HTTP 503 backoff after successful
  processing" fired once per successfully processed station — tens of thousands of lines per cycle. The
  503 *record* is still a warning. **This is the only log level that changed; see the policy below.**

### Log-level policy (added with this release)

**Startup logs that prove a successful start stay at `Information`.** A deployment must be verifiable
without turning on `Debug`:

| Log | Level |
|---|---|
| `Scheduled station cycle. cron="…"` | `Information` |
| `StartupVerificationStations enabled — processing N station(s)` | `Information` |
| `Startup verification: station {MLI} processed successfully.` | `Information` |
| `Startup verification: SUCCESS — N station(s) processed successfully.` | `Information` |
| `Startup verification: FAILED — no stations were processed successfully.` | `Error` |
| `RunOnStartup enabled — running an immediate full cycle before scheduling.` | `Information` |
| `Station pass completed` / `Station cycle completed` / `Station cycle duration` | `Information` |
| per-station chatter (processed station, 503 backoff reset) | `Debug` |

Pinned by `StationWorkerTests.StartupVerification_ReportsSuccessAtInformation`,
`StartupVerificationFailure_IsReportedAtError` and `RunOnStartup_AnnouncesItselfAtInformation`, so a future
log-noise cleanup cannot quietly demote them. **Per-station chatter may be demoted; startup verification
may not.**

### Also in this release (committed earlier, never released)

- Test framework moved from xUnit to **TUnit**, and native AOT enabled (`48061ea`, 2026-07-24) — committed
  after the `10.0.5` image was built, so this is its first release.

---

> **Entries below 10.1.0 were reconstructed on 2026-08-06** from git history and the image build dates on
> the droplet (`docker images` for this service) — this file did not exist while
> they were released, and no `10.0.4` image was ever built. Dates are **image build dates** (when the
> release actually shipped), which run ahead of the commit dates. Treat the grouping as best-effort.

## [10.0.5] — 2026-07-21

The release running in production until `10.1.0`.

### Added

- **Per-station HTTP 503 backoff** for water stations (`18e64d7`) — a station returning HTTP 503 is backed
  off rather than retried every cycle.

### Changed

- Startup verification now requires **both** the CA and US workers to process at least one station each
  (`cc257dd`), so a half-broken deployment can no longer report success.

## [10.0.3] — 2026-07-21

### Changed

- **Log noise: silenced framework/retry telemetry.** Polly logged each *handled* retry at Warning **with a
  full stack trace** (e.g. a transient USGS connect timeout that then recovered). `Polly` is now overridden
  to `Error`, which kills the handled-retry noise while leaving genuine unhandled Polly errors visible.
- Added concise circuit-breaker state-change logging in `ResiliencePipelines` (`OnOpened` / `OnClosed` /
  `OnHalfOpened`) — one line per state change instead of per attempt.
- `StationProcessorBase` logs failures as `Type: message` with no stack trace.

Net effect: a retried-then-recovered station produces **0** log lines; a fully failed one produces **1**
short line.

## [10.0.2] — 2026-07-18

### Changed

- **Log flood fixed.** Default `HttpClient` and Polly logging wrote **212 MB per cycle** — every request
  and every attempt logged its timing. `System.Net.Http.HttpClient` and `Polly` are now overridden to
  `Warning`, with a 20 MB per-file cap (`fileSizeLimitBytes` + `rollOnFileSizeLimit`, 7 files retained).

## [10.0.1] — 2026-07-18

### Added

- `Water:Worker:RunOnStartup` (default `false`; env `Water__Worker__RunOnStartup=true`) — runs one full
  cycle immediately on container start instead of waiting up to an hour for the first cron fire.

### Changed

- **Full scale:** processes **all** stations (~2,219 CA + ~9,501 US), matching the Java service. A full
  cycle takes ~2–3 h and therefore overruns the hourly cron (see the overrun metric).

## [1.0.0] — 2026-07-17

### Added

- Initial **C# / .NET 10 port** of the Java/Spring Boot `water-station-pusher`
  (`efj-backend/service/waterservice`). Same stored procedures (`sp_UpdateWaterData`,
  `sp_push_us_water_data`, `sp_clean_old_water_data`, `spPushSpeciesFromLakeToStation`) and the same
  `vwWaterStation` query. Stack: Microsoft.Data.SqlClient, Polly, CsvHelper, Cronos, Serilog,
  prometheus-net. `--console [--station=<MLI>]` runs a single cycle. A JDBC→SqlClient converter lets the
  shared `.env` `DB_URL` work unchanged.
- Docker: self-contained `linux-x64` on `debian:trixie-slim`, non-root uid 10001.
- Documented the **intentional Java + C# dual-service redundancy** — both services run in parallel against
  the same database by design. **Do not retire either.**

---

## Changelog rules

- **This file is the single place for changelog entries for this service.** Do not start a
  `## Changelog` section in `AGENTS.md`, `README.md`, or `docs/specification.md` — link here instead.
- Add to `## [Unreleased]` as you work; promote it to a version heading when a tag is cut, and bump
  `<Version>` in `WaterService/WaterService.csproj` in the same change.
- Group entries under `Added` / `Changed` / `Fixed` / `Removed` / `Security`.
- Say **why**, not just what — a future reader needs the reason a behaviour changed, especially for log
  levels, retry/backoff semantics, and anything that alters what a deployment looks like.
- `docs/specification.md` stays a description of the **current** state; this file carries the **history**.
  A change usually touches both.
- Note anything that changes deployment (new mounts, env vars, required secrets) and cross-reference
  `docs/do-update.md`.
