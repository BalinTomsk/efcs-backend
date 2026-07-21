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

## Keeping docs in sync — IMPORTANT

`docs/specification.md` is the **single source of truth** used to recreate this service from scratch.
It must always reflect the current state of the code.

**Rules:**

- Whenever **any source file** (`*.cs`, `WaterService.slnx`, `WaterService.csproj`, `launchSettings.json`,
  `Dockerfile`, etc.) is created, modified, or deleted — update `docs/specification.md` to match.
- Whenever **this `claude.md`** is updated — apply the same change to `docs/specification.md`
  if it affects behaviour, structure, or configuration.
- `docs/specification.md` must be sufficient on its own for a developer (or Claude) to
  **fully recreate the service from scratch** with no other context. Keep it complete and accurate.
- Do not leave `docs/specification.md` describing behaviour that no longer exists, or omitting
  behaviour that was added.
- Treat every code change as a two-step commit: ① change the code, ② update `docs/specification.md`.

---

##IMPORTANT
Explicitly follows database schema at:
- @srv/../../envfish-db

- **DO NOT COMMIT without explicit user permission.**
- **DO NOT PUSH without explicit user permission.**
- **DO NOT CREATE, MERGE, OR CLOSE PULL REQUESTS without explicit user permission.**
- When code changes are requested, make the file edits and stop with a status summary unless the
  user explicitly asks for Git actions.

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

## Local Claude skills

- Deployment skill: `.claude/skills/update-water/SKILL.md`
- Use `update-water` when asked to deploy/update/release `water-station-pusher`, build and push a tagged Docker image, install it on the DigitalOcean droplet, or verify the deployed service.
- A version tag is required. If the user does not provide one, ask for it before running deployment commands.
- The deployment runbook/source of truth is `docs/do-update.md`; keep it aligned with the skill before deploying.

---

## Project identity

| Key | Value |
|-----|-------|
| Service | `water-station-pusher` (C# / .NET 10 port) |
| Local Docker engine | Rancher Desktop (dockerd inside the `rancher-desktop` WSL VM) |
| Registry | GitHub Container Registry (GHCR) |
| GHCR image | `ghcr.io/balintomsk/water-station-pusher-cs:<TAG>` |
| GHCR user | `BalinTomsk` |
| Droplet | `root@137.184.218.128` (`debian-csnode`, Debian 13, amd64) |
| Container name | `water-station-pusher-cs` |
| Attached volume | `volume-env` → `/mnt/volume_env` (ext4; persisted via a `nofail` fstab entry) |
| Env file on droplet | `/mnt/volume_env/waterservice/waterservice.env` (owner `10001:10001`, mode `0400`) |
| Container env mount | `/run/secrets/waterservice.env` (read via `DOTENV_PATH`) |
| Logs on droplet | `/mnt/volume_env/waterservice/logs` → `/app/logs` (Serilog JSON, daily roll, 7-day retention) |
| Published port | `8080` (health). `8081` (metrics/liveness/readiness) is **not** published |
---

## Goal

- Poll supported Canadian and US water stations from MSSQL (`vwWaterStation`).
- Download each **CA** station's hourly hydrometric CSV from Environment Canada.
- Download each **US** station's WaterML payload from USGS.
- Parse readings and upsert them into `dbo.WaterData`.
- After each worker cycle, synchronously run stale-data cleanup:
  1. `dbo.sp_clean_old_water_data`
- When at least one station succeeds in a cycle, also run:
  1. `dbo.spPushSpeciesFromLakeToStation`
- Log failures and skipped unpublished-source events; **do not disable stations automatically**.

---

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
