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
  each job's `… done`), so a deploy is verified without Debug.
- **Ports:** 8080 public (`/health`; later `/api/v1/env/*`), 8081 private (`/health/live`, `/health/ready`, `/metrics`,
  `/admin/*`), split by `Connection.LocalPort`, never `RequireHost`. Never publish 8081 beyond localhost.
- **Exposure (later REST):** through cproxy and the VPC only; every route under `/api/v1/env/`.
- **Sized before deploy:** memory cap + the 150 MB guard; verify by restart count, never a log tail.
- No `InvariantGlobalization`; option collections default to **empty**; tests in TUnit.

## Build & test

```bash
dotnet build EnvService.slnx
dotnet run --project EnvService.Tests            # 14 tests (job logic with fakes, TVP/JSON contracts, ports)
dotnet run --project EnvService.Migrator.Tests   # 11 tests (conversions)
```
