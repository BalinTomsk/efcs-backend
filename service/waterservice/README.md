# water-station-pusher (C# / .NET 10)

A background worker that pulls hydrometric data for Canadian and US water-monitoring stations and pushes
it into the FishFind MySQL `envdata` database (water/weather store; since 11.0.0, no SQL Server). This is the C# port of the original Java/Spring Boot service,
built to run in Docker on Debian 13 ("trixie").

## What it does

Once per hour (cron-driven) it:

1. Loads supported stations from `v_station_water_due` (split by country `CA` / `US`).
2. Downloads each **CA** station's hourly hydrometric CSV from Environment Canada, parses it, and upserts
   readings via `sp_water_save_ca` (water level and discharge, 500 readings per call as JSON).
3. Fetches one snapshot of the latest US values from the USGS Water Data API per cycle (`USGS_API_KEY`), picks
   out each **US** station, and saves all its series in one `sp_water_save_us` call under their legacy USGS names.
4. When at least one station succeeded, records the cycle in `job_run` (`sp_job_run_record`); env_service does the
   fish hand-over to SQL Server.
5. After every cycle, runs `sp_clean_old_water_data` (retention by day partition).

Failures are logged and isolated per station — stations are never auto-disabled.

## Tech

- **.NET 10** (Worker + minimal ASP.NET Core host for `/health` and metrics)
- **MySqlConnector** — MySQL `envdata` access via its procedures and views (no ORM)
- **Polly** — retry + circuit breaker (Resilience4j equivalent)
- **CsvHelper** — fault-tolerant CSV parsing; `System.Text.Json` for the USGS Water Data API
- **Cronos** — cron scheduling
- **Serilog** — structured JSON logs, 7-day rolling file
- **prometheus-net** — `/metrics`

## Configuration

Credentials come from the environment (or a local `.env` as a lowest-precedence fallback):

| Key | Meaning |
|-----|---------|
| `MYSQL_URL` | envdata: `jdbc:mysql://host:3306/envdata?sslMode=REQUIRED` or a native MySqlConnector string |
| `MYSQL_USERNAME` / `MYSQL_PASSWORD` | MySQL account (ignored if the URL already carries them) |
| `DB_URL` / `DB_USERNAME` / `DB_PASSWORD` | the SQL Server connection; kept in the file, not opened by 11.0.0 |

Worker behaviour is in `appsettings.json` under `Water:Worker` (override with env vars like
`Water__Worker__Cron`). See `.env.example`.

## Build & test

```bash
dotnet build
dotnet run --project WaterService.Tests
```

The test project uses **TUnit**, which runs as an executable — `dotnet test` does not run it. Set
`WATER_TEST_MYSQL` to a throwaway envdata database's connection string to also run the engine tests.

Release history and the log-level policy live in [`CHANGELOG.md`](CHANGELOG.md).

## Run locally

```bash
# copy .env.example -> .env and fill in MYSQL_URL / MYSQL_USERNAME / MYSQL_PASSWORD (and DB_* for SQL Server)
dotnet run --project WaterService
# then: curl http://localhost:8080/health
```

Run a single cycle and exit (no scheduler), optionally for one station:

```bash
dotnet run --project WaterService -- --console
dotnet run --project WaterService -- --console --station=05BB001
```

## Docker

```bash
docker build -t water-station-pusher:local .
docker run --rm -p 8080:8080 --env-file .env water-station-pusher:local
```

- Runtime base is **Debian 13 ("trixie")**. Because GA .NET 10 has no Debian-trixie image yet, the app
  is published self-contained for `linux-x64` and runs on `debian:trixie-slim` (no .NET runtime on the
  base). See [`docs/specification.md`](docs/specification.md#docker).
- `/health` (port **8080**) is the container HEALTHCHECK target and the only surface to publish.
- Metrics + liveness/readiness are on port **8081** — keep it private.
- Runs as a non-root user (uid 10001); mount a volume/tmpfs at `/app/logs` for a read-only root filesystem.

## Endpoints

| Port | Path | Purpose |
|------|------|---------|
| 8080 | `/health` | `{ status, version, uptime }` — external probe |
| 8081 | `/health/live` | liveness (process only; not DB-dependent) |
| 8081 | `/health/ready` | readiness (includes datasource connectivity) |
| 8081 | `/metrics` | Prometheus metrics |

See [`docs/specification.md`](docs/specification.md) for the full specification.
