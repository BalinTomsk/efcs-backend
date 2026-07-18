# water-station-pusher (C# / .NET 10)

A background worker that pulls hydrometric data for Canadian and US water-monitoring stations and pushes
it into the FishFind SQL Server database. This is the C# port of the original Java/Spring Boot service,
built to run in Docker on Debian 13 ("trixie").

## What it does

Once per hour (cron-driven) it:

1. Loads supported stations from `vwWaterStation` (split by country `CA` / `US`).
2. Downloads each **CA** station's hourly hydrometric CSV from Environment Canada, parses it, and upserts
   readings via `dbo.sp_UpdateWaterData` (water level → `elevation`, discharge → `discharge`).
3. Downloads each **US** station's WaterML from USGS, reduces it to the latest sample per day per variable,
   and saves it via `dbo.sp_push_us_water_data`.
4. When at least one station succeeded, runs `dbo.spPushSpeciesFromLakeToStation`.
5. After every cycle, runs `dbo.sp_clean_old_water_data`.

Failures are logged and isolated per station — stations are never auto-disabled.

## Tech

- **.NET 10** (Worker + minimal ASP.NET Core host for `/health` and metrics)
- **Microsoft.Data.SqlClient** — SQL Server access via the legacy stored procedures (no ORM)
- **Polly** — retry + circuit breaker (Resilience4j equivalent)
- **CsvHelper** — fault-tolerant CSV parsing; hardened `XmlReader` for WaterML (XXE-safe)
- **Cronos** — cron scheduling
- **Serilog** — structured JSON logs, 7-day rolling file
- **prometheus-net** — `/metrics`

## Configuration

Credentials come from the environment (or a local `.env` as a lowest-precedence fallback):

| Key | Meaning |
|-----|---------|
| `DB_URL` | JDBC-style URL (`jdbc:sqlserver://host:1433;databaseName=db;...`) or a native SqlClient string |
| `DB_USERNAME` / `DB_PASSWORD` | SQL login (ignored if the URL already carries them) |

Worker behaviour is in `appsettings.json` under `Water:Worker` (override with env vars like
`Water__Worker__Cron`). See `.env.example`.

## Build & test

```bash
dotnet build
dotnet test
```

## Run locally

```bash
# copy .env.example -> .env and fill in DB_URL / DB_USERNAME / DB_PASSWORD
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
