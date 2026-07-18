# water-station-pusher (.NET 10) — Specification

> Source of truth for recreating this service from scratch. Keep it in sync with the code: every source
> change should be reflected here. This is the C# port of the original Java/Spring Boot service.

---

## Project identity

| Key | Value |
|-----|-------|
| Service name | `water-station-pusher` |
| Language | C# / .NET 10 |
| Host | Generic Host + minimal ASP.NET Core (Kestrel) for `/health` + metrics |
| Assembly | `WaterService.dll`, root namespace `WaterService` |
| Runtime target | Docker on Debian 13 ("trixie") |

## Goal

- Poll supported CA/US water stations from MSSQL (`vwWaterStation`).
- Download each CA station's hourly hydrometric CSV from Environment Canada.
- Download each US station's WaterML from USGS.
- Parse readings and upsert them via legacy stored procedures.
- After each cycle, run stale-data cleanup (`dbo.sp_clean_old_water_data`).
- When ≥1 station succeeds, also run `dbo.spPushSpeciesFromLakeToStation`.
- Log failures and skipped unpublished-source events; **never auto-disable stations**.

## Layout

```
WaterService.slnx
WaterService/
  Program.cs                         # entry point: web mode + --console mode + Serilog config
  appsettings.json                   # Water:Worker section
  Domain/{Reading,StationRef,UsSeriesReading}.cs
  Configuration/
    WorkerOptions.cs                 # bound from Water:Worker
    DotEnvLoader.cs                  # local .env -> lowest-precedence config
    JdbcConnectionString.cs          # JDBC URL -> SqlClient connection string
    ResiliencePipelines.cs           # Polly sql / caFeed / usFeed pipelines
    ServiceRegistration.cs           # DI wiring shared by both hosting modes
  Data/
    ISqlConnectionFactory.cs, SqlConnectionFactory.cs
    WaterStationRepository.cs        # vwWaterStation query
    WaterDataRepository.cs           # stored-procedure upserts + post-processing
  Sources/{CsvFetcherCA,XmlFetcherUS}.cs
  Processing/
    StationProcessorBase.cs, StationProcessorCA.cs, StationProcessorUS.cs
    StationPostProcessingService.cs
    StationWorker.cs                 # BackgroundService: cron schedule + parallel passes + console cycle
  Web/{AppInfo,DbHealthCheck,WaterMetrics}.cs
WaterService.Tests/                  # xUnit
Dockerfile, .dockerignore, .env.example, .gitignore, README.md
```

## Startup / credentials

1. `Program.cs` loads a local `.env` (if present) and inserts it as the **lowest-precedence** config
   source, so real environment variables and `appsettings.json` always win. Only keys declared in the
   file are imported. (`DOTENV_PATH` overrides the default `./.env` location.)
2. The connection string is built from `DB_URL` / `DB_USERNAME` / `DB_PASSWORD`. `DB_URL` may be a
   JDBC URL (`jdbc:sqlserver://host:1433;databaseName=db;encrypt=...;trustServerCertificate=...`) — it
   is converted to a SqlClient connection string (`host:port` → `host,port`) — or a native SqlClient
   string. Credentials in the URL are not overwritten by `DB_USERNAME`/`DB_PASSWORD`.
3. Secrets are never written to a committed file or logged.

## Hosting modes

- **Normal (web) mode** — `WebApplication` on Kestrel bound to `0.0.0.0:8080` and `0.0.0.0:8081`. The
  `StationWorker` `BackgroundService` schedules the cron cycle. Endpoints:
  - `8080 /health` → `{ status:"UP", version, uptime }` (external probe / Docker HEALTHCHECK).
  - `8081 /health/live` → liveness, process-only (no checks; never DB-dependent).
  - `8081 /health/ready` → readiness, includes the `db` check (SELECT 1).
  - `8081 /metrics` → Prometheus.
  - Endpoints are isolated per port via `RequireHost("*:8080" | "*:8081")`.
- **Console mode** — `--console [--station=<MLI>]` builds a non-web Host, runs exactly one cycle via
  `StationWorker.RunCycleAsync`, and exits. Identical behaviour to a scheduled cycle.

## Worker behaviour (`StationWorker`)

- `Water:Worker:Enabled=false` disables scheduling entirely.
- The scheduler loop computes the next occurrence of `Water:Worker:Cron` (6-field, seconds-first;
  default `0 0 * * * *` = top of every hour), waits, runs one cycle, then records the outcome. A single
  loop means an overrunning cycle delays the next one instead of overlapping.
- **Cycle** (`RunCycleAsync`):
  1. Run CA and US passes **in parallel**; a failure of one country is isolated and logged.
  2. Each pass loads its stations and processes them one by one, pausing
     `Water:Worker:PauseBetweenStationsMs` **between** stations only (cancellation-aware).
  3. Run species post-processing **once**, and **only if ≥1 station succeeded**.
  4. Run stale-data cleanup **once after every cycle**.
  5. A species-push failure never blocks cleanup; if cleanup also fails, the failures are combined
     (`AggregateException`) and thrown after the cycle-completed summary log is emitted.
- **Overrun visibility:** if a cycle finishes at/after the next scheduled fire time, increment
  `water_cycle_overrun_total` and log a warning; otherwise log the cycle duration.

## Station query (`WaterStationRepository`)

`SELECT mli, state, tz FROM vwWaterStation WHERE country = @country ORDER BY stamp DESC`
→ `StationRef(string Mli, string State, int Tz)`.

## HTTP transport

- Shared, pooled `HttpClient` named `waterSource` via `IHttpClientFactory`; `SocketsHttpHandler` with
  connect timeout (`ConnectTimeoutMs`), request timeout (`ReadTimeoutMs`), auto-decompression.
- Honest `User-Agent` (`Water:Worker:UserAgent`) — not a spoofed browser string.

### CA fetch (`CsvFetcherCA`)

- URL: `https://dd.weather.gc.ca/today/hydrometric/csv/{STATE}/hourly/{STATE}_{MLI}_hourly_hydrometric.csv`
- `state`/`mli` are `Uri.EscapeDataString`-encoded (no path injection from a hostile DB row).
- 404 → `FileNotFoundException` (station skipped; not retried, not counted against the breaker).
- Wrapped in the `caFeed` resilience pipeline.

### US fetch (`XmlFetcherUS`)

- URL: `https://waterservices.usgs.gov/nwis/iv/?sites={MLI}&period=P3D&format=waterml`
- `mli` is URL-encoded (cannot inject query params). 404 → `FileNotFoundException`.
- Wrapped in the `usFeed` resilience pipeline.

## Parsing

- **CA CSV (`StationProcessorCA`, CsvHelper):** trim; skip header row 0; columns `[0]` station id,
  `[1]` timestamp (`DateTimeOffset`), `[2]` water level, `[6]` discharge. Fault-tolerant: short rows,
  blank station/timestamp, and unparseable rows are skipped (not the whole batch) and counted on
  `water_csv_rows_skipped_total{country}`. → `Reading(StationId, Stamp, WaterLevel?, Discharge?)`.
- **US WaterML (`StationProcessorUS`, LINQ-to-XML):** one payload per variable. Reduce values to daily
  entries keeping the **latest sample by timestamp** per day (document order not trusted). Emit legacy
  `<root><a d="yyyy-MM-dd" v="..." /></root>`. **XXE-hardened:** `XmlReader` with
  `DtdProcessing=Prohibit` and `XmlResolver=null` — a DOCTYPE/entity payload is rejected, never expanded.

## Persistence (`WaterDataRepository`)

- `SaveStationDataAsync(mli, readings)` — reject blank `mli`; no-op if empty; de-duplicate by timestamp
  (last wins); one transaction; upsert each reading via `dbo.sp_UpdateWaterData` (@mli, @stamp `datetime2`,
  @elevation `float`, @discharge `float`). Timestamps stored as UTC wall-clock (container runs in UTC).
  The 15-day retention delete lives inside the stored procedure.
- `SaveUsStationDataAsync(mli, state, seriesList)` — `dbo.sp_push_us_water_data` (@mli, @state, @name,
  @unit, @xmldoc `xml`); empty payloads ignored.
- `PushSpeciesFromLakeToStationAsync()` → `dbo.spPushSpeciesFromLakeToStation`.
- `CleanOldWaterDataAsync()` → `dbo.sp_clean_old_water_data`.
- All SQL is wrapped in the `sql` resilience pipeline. `ExecuteNonQuery` tolerates procedures that emit
  incidental result sets.

## Resilience (`ResiliencePipelines`, Polly)

Circuit breaker is the **outermost** strategy in every pipeline (added before retry), so an open breaker
short-circuits without burning retries and all retries of one call count as a single breaker outcome.

- `sql` — retry 3 attempts / 2s (SqlException/DbException/TimeoutException) + breaker (50% failure ratio,
  min throughput 5, 30s sampling, 30s break).
- `caFeed` / `usFeed` — separate breakers (50%, min 10, 30s/30s) + shared retry 3 / 2s. Both retry only
  transient HTTP failures (HttpRequestException, request timeouts) and **ignore** `FileNotFoundException`
  (404 → source-not-published), so 404-skips are neither retried nor counted against the breaker.

## Logging (Serilog)

- Structured **JSON** to console + a rolling file (`logs/water-station-pusher.log`, daily,
  `retainedFileCountLimit: 7` ⇒ ~7-day retention).
- Every entry carries `service=water-station-pusher` and a timestamp/level; `correlationId` (per cycle)
  and `station` (per station) are bound via the logging context.
- Levels: root `Information`; `Microsoft` namespaces `Warning`.

## Metrics (prometheus-net)

- `water_station_processed_total{country,outcome}`
- `water_csv_rows_skipped_total{country}`
- `water_cycle_overrun_total`
- plus default process/runtime metrics — all on `8081 /metrics`.

## Docker

- Multi-stage build → **Debian 13 ("trixie")** runtime. GA .NET 10 does not (yet) ship a Debian-trixie
  base image (the default `10.0` tag is Ubuntu Noble; trixie exists only as `-preview-` tags), so the
  service is published **self-contained** for `linux-x64` and runs on the official `debian:trixie-slim`
  — no .NET runtime installed on the base. Pin the runtime base by digest for reproducibility.
  - Build stage: `mcr.microsoft.com/dotnet/sdk:10.0`, `dotnet publish -r linux-x64 --self-contained true`.
  - Runtime stage: `debian:trixie-slim` + `apt-get install ca-certificates curl openssl libicu76`.
    **ICU is required** — Microsoft.Data.SqlClient throws "Globalization Invariant Mode is not
    supported" at connection open, so `InvariantGlobalization` must NOT be set; openssl provides libssl
    for SqlClient TLS.
  - Entry point is the native apphost `/app/WaterService`.
- `curl` is used for the HEALTHCHECK (`curl -fsS http://localhost:8080/health`).
- Runs as a non-root user (uid/gid 10001); `/app/logs` pre-created and owned by it (read-only-rootfs
  friendly — mount a volume/tmpfs there). Container timezone `UTC`.
- Never bake `.env`/secrets into the image (see `.dockerignore`).
