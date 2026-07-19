# water-station-pusher (C# / .NET 10) — Claude Context

C# / .NET 10 port of the Java/Spring Boot `water-station-pusher` (`efj-backend/service/waterservice`). A
cross-platform worker that pulls hydrometric data for **all** Canadian (Environment Canada CSV) and US
(USGS WaterML) stations and upserts it into the FishFind SQL Server via the legacy stored procedures,
running in Docker on **Debian 13**.

## Dual-service redundancy (Java + C#) — do NOT retire either

**Two independent implementations of this service run in parallel in production, by design.** This C# port
and the original **Java (Spring Boot)** service (`efj-backend/service/waterservice`) both poll the same
Environment Canada / USGS feeds *independently* and upsert into the **same** database. This is an
**intentional "double warranty"** on incoming data — redundancy, **not** a migration. **Keep both running;
do not decommission either one to "avoid double-writes."**

Why running both is safe: writes go through `dbo.sp_UpdateWaterData`, keyed by `(mli, stamp)` as an upsert,
so concurrent writes from the two services collapse to the same rows. If one pipeline is down, slow, or a
feed fetch fails on one side, the other still lands the data. The post-processing procs
(`dbo.sp_clean_old_water_data`, `dbo.spPushSpeciesFromLakeToStation`) therefore run once per service per
cycle; that duplication is expected and tolerated.

Deployments: this C# service runs on droplet **`debian-csnode`** (`137.184.218.128`); the Java service runs
on **`debian-jnode`** (`68.183.196.166`). Each repo has its own `docs/do-update.md`.

##IMPORTANT
Explicitly follows database schema at:
- @srv/../../envfish-db

- Local project skills live under `.claude/skills` inside this service. When the user asks to run or
  use a skill by name, you MUST first look for and use `.claude/skills/<skill-name>/SKILL.md`.
  Only search repo-level `Skills` directories or global skill registries if that project-level file
  does not exist.

- **Before making ANY database change** (schema, stored proc, function, view, seed data, or any
bug fix that touches the DB), **read `c:\envoinx\fishfind\envfish-db\CLAUDE.md `
first** — it is the authoritative DB workflow (never edit the generated `ffi2.sql`; edit the
`scriptNN_*.sql` sources; test-first: a FAILING unit test to confirm the bug, then a PASSING one
to verify the fix; run `mssql\UNIT_TESTS\autorun.bat`). That file lives in the separate
`efch-backend` repo and does NOT auto-load in this project, so it must be opened explicitly.



## Orientation

- **Source of truth:** `docs/specification.md` (full spec — keep it in sync with the code). `README.md` for
  build/run. Deployment runbook: `docs/do-update.md`.
- **Stack:** Microsoft.Data.SqlClient (no ORM), Polly (retry + circuit breaker), CsvHelper, Cronos, Serilog
  (JSON, 7-day rolling file), prometheus-net. Host: Generic Host + minimal ASP.NET Core.
- **Layout:** `Program.cs` (web mode + `--console` one-shot); `Processing/StationWorker.cs` (cron scheduler,
  parallel CA/US passes, `RunOnStartup`); `Sources/` (CA CSV + US WaterML fetchers); `Processing/` (station
  processors — US XML is XXE-hardened); `Data/` (repositories calling the same procs: `sp_UpdateWaterData`,
  `sp_push_us_water_data`, `sp_clean_old_water_data`, `spPushSpeciesFromLakeToStation`); `Configuration/`
  (options, `.env` loader, JDBC→SqlClient converter, Polly pipelines); `Web/` (`/health`, metrics, DB check).
- **Endpoints:** `/health` on **8080** (public probe); metrics + liveness/readiness on **8081** (private,
  never publish).

## Build & test

```bash
dotnet build
dotnet test        # 13 xUnit tests
```

Docker: multi-stage build publishing a **self-contained linux-x64** app onto `debian:trixie-slim`
(GA .NET 10 has no Debian-trixie base image), non-root uid 10001. **Do not set `InvariantGlobalization`** —
Microsoft.Data.SqlClient requires ICU (`libicu76` is installed in the image).

## Keeping docs in sync — IMPORTANT

`docs/specification.md` must always reflect the current state of the code. Treat every source change as two
steps: ① change the code, ② update `docs/specification.md` (and this file / `docs/do-update.md` if behavior,
structure, or deployment changed).

## Secrets

DB credentials come from `DB_URL` / `DB_USERNAME` / `DB_PASSWORD` (real environment variables, or a local
`.env` as a lowest-precedence fallback). `DB_URL` may be a JDBC-style URL (converted to a SqlClient
connection string) for parity with the other backend services. **Never commit real credentials** — committed
files and `docs/*` use placeholders only. On the droplet the secret is a volume-mounted file read via
`DOTENV_PATH`.
