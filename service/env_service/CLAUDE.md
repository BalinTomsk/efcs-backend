# env_service (C# / .NET 10) — Claude Context

The water/weather service of FishFind. It owns no ingest (waterservice / weather write MySQL `envdata`); it is the
**only bridge** between `envdata` and SQL Server, and later the REST API for every water/weather/forecast read.
Plan with every decision: `docs/env_service_plan.md`. Built like `../waterapi`, which it copies for its host.

**Source of truth:** `docs/specification.md`. Session log: `docs/memory.md`. History: `CHANGELOG.md`.
Deploy runbook: `docs/do-update.md`. Endpoint contracts: `docs/api-reference.html`. `docs/` is git-ignored
(host addresses stay off this public repo).

## Rules

- **DO NOT COMMIT / PUSH / open-merge-close PRs without explicit user permission.**
- **Keep docs in sync.** Every source change is ① code, ② `docs/specification.md`; behaviour changes also get a
  `CHANGELOG.md` entry under `[Unreleased]` with the reason; an endpoint contract change updates
  `docs/api-reference.html` in the same change (and cproxy `api-guide.html` + Postman together when a gateway path
  changes).
- **Version numbers come from `EnvService/EnvService.csproj`** (`EnvService.Migrator.csproj` for the migrator).
- **No table access, in either database.** envdata: its procedures and views only (`envfish-db/mysql/envdata`).
  SQL Server: schema `env` only (`env.fn_station_list`, `env.sp_*`; `envfish-db/mssql`, the [env] blocks). Before any
  database change read `c:\envoinx\fishfind\envfish-db\AGENTS.md` (test-first; edit `scriptNN_*.sql`).
- **Connections:** `MYSQL_URL`/`MYSQL_USERNAME`/`MYSQL_PASSWORD` = envdata, `DB_URL`/`DB_USERNAME`/`DB_PASSWORD` = SQL
  Server; both required. Pressure is mm Hg everywhere.
- **The fish model is behind a switch (plan step 5):** the hand-over calls `env.sp_push_species` and
  `env.sp_update_probability`, which do nothing until SQL Server's `env.Setting probability_source` is `env`; then the
  legacy `dbo.spTotalUpdateProbability` returns at once. Never bypass the switch from C#.
- **A failure never loses work:** pending `job_run` cycles are marked handed over only after every push succeeded.
- **Startup lines at `Information`** (`env_service … starting`, `Station mirror started`, `Hand-over started`,
  `Station cache started`, each job's `… done`), so a deploy is verified without Debug.
- **Ports:** 8080 public (`/health`, `/api/v1/env/*`), 8081 private (`/health/live`, `/health/ready`, `/metrics`,
  `/admin/*`), split by `Connection.LocalPort`, never `RequireHost`. Never publish 8081 beyond localhost.
- **Reads (`/api/v1/env/*`):** validate first (400 before any read); stations only from `StationCache` (never a
  per-request station scan); everything else a primary-key range of an envdata view through the `sql-read` pipeline;
  a failed refresh keeps the last snapshot, 503 only before the first load. Metric, pressure mm Hg, times UTC.
- **Forecast (`/api/v1/env/forecast/*`):** the fish half only from SQL Server `env.fn_*` functions (never a new
  SQL Server TABLE: on 2026-11-24 `env.*` tables took the website down through its schema cache), the water and
  weather half from envdata views; docapi's parameter rules; check `dbo.LogException` after any prod SQL apply.
- **Exposure (later REST):** through cproxy and the VPC only; every route under `/api/v1/env/`.
- **Sized before deploy:** memory cap + the 150 MB guard; verify by restart count, never a log tail.
- No `InvariantGlobalization`; option collections default to **empty**; tests in TUnit.

## Build & test

```bash
dotnet build EnvService.slnx
dotnet run --project EnvService.Tests            # 36 tests (job logic and reads with fakes, TVP/JSON contracts, ports)
dotnet run --project EnvService.Migrator.Tests   # 11 tests (conversions)
```
