# water-station-pusher (C# / .NET 10) — Claude Context

C# / .NET 10 port of the Java/Spring Boot `water-station-pusher` (`efj-backend/service/waterservice`). A
cross-platform worker that pulls hydrometric data for **all** Canadian (Environment Canada CSV) and US
(USGS Water Data API) stations and upserts it, since 11.0.0, into the MySQL **`envdata`** database (debian-mysql,
schema `envfish-db/mysql/envdata`) through its procedures, running in Docker on **Debian 13**. It no longer talks
to SQL Server: env_service hands the data over to the fish model there (plan:
`efcs-backend/service/env_service/docs/env_service_plan.md`).

## Java twin: shadow period, then retired (changed 2026-11-24)

Until 10.8.0 this C# port and the **Java (Spring Boot)** service (`efj-backend/service/waterservice`) both wrote
the same SQL Server tables as a "double warranty". **That ended with the move to MySQL (user decision
2026-11-24):** from 11.0.0 this service writes MySQL `envdata`; the Java service keeps writing SQL Server during
the **shadow period** (comparison and fallback, and it still runs the species push there) and is retired at
cut-over (plan step 7). The two no longer share any state: not the readings, not the 503 back-off. During the
shadow period each pair uses its **own USGS API key** (the key's 1,000/h is per key).

## Changelog — use `CHANGELOG.md`

**`CHANGELOG.md` (service root) is the single place for changelog entries.** Every notable change goes
there under `## [Unreleased]`, grouped `Added` / `Changed` / `Fixed` / `Removed` / `Security`, with the
**reason** for the change — not just what changed. Do **not** start a `## Changelog` section in this file,
`README.md`, or `docs/specification.md`; link to `CHANGELOG.md` instead. `docs/specification.md` describes
the **current** state, `CHANGELOG.md` carries the **history**. When a version is tagged, promote
`[Unreleased]` to a version heading and bump `<Version>` in `WaterService/WaterService.csproj` in the same
change. `CHANGELOG.md` also carries the **log-level policy** (below) — read it before touching log levels.

## Log levels — startup verification stays INFO

Per-station chatter may be demoted to `Debug` to control volume, **but the logs that prove a successful
start must stay at `Information`** so a deployment can be verified without enabling `Debug`: the
`Scheduled station cycle` line, all `Startup verification: …` progress/SUCCESS lines (FAILED is `Error`),
`RunOnStartup enabled …`, and the pass/cycle completion summaries. This is pinned by
`StationWorkerTests.StartupVerification_ReportsSuccessAtInformation` and its two siblings — if one of those
fails, a log-noise cleanup went too far. Full table in `CHANGELOG.md`.

## Keeping docs in sync — IMPORTANT

`docs/specification.md` is the **single source of truth** used to recreate this service from scratch.
It must always reflect the current state of the code.

**Rules:**

- Whenever **any source file** (`*.cs`, `WaterService.slnx`, `WaterService.csproj`, `launchSettings.json`,
  `Dockerfile`, etc.) is created, modified, or deleted — update `docs/specification.md` to match.
- Whenever **this `AGENTS.md`** is updated — apply the same change to `docs/specification.md`
  if it affects behaviour, structure, or configuration.
- Whenever behaviour changes, also add an entry to `CHANGELOG.md` under `## [Unreleased]`.
- `docs/specification.md` must be sufficient on its own for a developer (or Claude) to
  **fully recreate the service from scratch** with no other context. Keep it complete and accurate.
- Do not leave `docs/specification.md` describing behaviour that no longer exists, or omitting
  behaviour that was added.
- Treat every code change as a two-step commit: ① change the code, ② update `docs/specification.md`.
- Give variables, functions, modules, etc. names that unambiguously indicate their purpose
- Use ASD-STE100 for instructions and answers


---

##IMPORTANT
Explicitly follows database schema at:
- @srv/../../envfish-db

- **DO NOT COMMIT without explicit user permission.**
- **DO NOT PUSH without explicit user permission.**
- **DO NOT CREATE, MERGE, OR CLOSE PULL REQUESTS without explicit user permission.**
- **When add/delete/update functionality create PR to make the same change in C# clone of this service if applicable in ..\..\..\efj-backend\service\waterservice **
- When code changes are requested, make the file edits and stop with a status summary unless the
  user explicitly asks for Git actions.

---

## Git and database

Database schema lives at:
- @srv/../../envfish-db

- **Do not commit, push, or create/merge/close pull requests without explicit user permission.** Make
  the file edits, then stop with a status summary unless the user asks for Git actions.

- **Before making ANY database change** (schema, stored proc, function, view, seed data, or any
bug fix that touches the DB), **read `c:\envoinx\fishfind\envfish-db\AGENTS.md `
first** — it is the authoritative DB workflow (never edit the generated `ffi2.sql`; edit the
`scriptNN_*.sql` sources; test-first: a FAILING unit test to confirm the bug, then a PASSING one
to verify the fix; run `mssql\UNIT_TESTS\autorun.bat`). That file lives in the separate
`efch-backend` repo and does NOT auto-load in this project, so it must be opened explicitly.

## Deployment

- The `update-water` skill deploys this service; its own description covers when it applies.
- `docs/do-update.md` is the runbook and the source of truth — keep it aligned with the skill before deploying.

---

## Project identity

| Key | Value |
|-----|-------|
| Service | `water-station-pusher` (C# / .NET 10 port) |
| Local Docker engine | Rancher Desktop (dockerd inside the `rancher-desktop` WSL VM) |
| Registry | GitHub Container Registry (GHCR) |
| Runtime target | Docker on Debian 13 (amd64), non-root uid 10001 |
| Env delivery | a mounted env file read via `DOTENV_PATH`; `enc:v1:` values need `FF_MASTER_KEY_FILE` |
| Logs | Serilog JSON to `/app/logs` (bind-mount a volume there), daily roll, 7-day retention |
| Published port | `8080` (health). `8081` (metrics/liveness/readiness) is **not** published |

> **Deployment specifics are deliberately not in this repo.** Host addresses, image coordinates, volume
> and secret paths live in `docs/do-update.md`, which is git-ignored and stays on the workstation. This
> repository is **public** — do not reintroduce them here, in `README.md`, or in `CHANGELOG.md`.

---

## Goal

- Poll supported Canadian and US water stations from envdata (`v_station_water_due`).
- Download each **CA** station's hourly hydrometric CSV from Environment Canada.
- Read each **US** station's latest values from one USGS Water Data API snapshot per cycle
  (`latest-continuous`; key in `USGS_API_KEY`). The legacy WaterServices feed dies 2027-02-22.
- Parse readings and upsert them through `sp_water_save_ca` / `sp_water_save_us` (envdata `water_reading`).
- After each worker cycle, synchronously run retention: `sp_clean_old_water_data`.
- When at least one station succeeds in a cycle, record it in `job_run` (`sp_job_run_record`); env_service does
  the fish hand-over to SQL Server from there.
- Log failures and skipped unpublished-source events; **do not disable stations automatically**.

---

## Orientation

- **Source of truth:** `docs/specification.md` (full spec — keep it in sync with the code). `README.md` for
  build/run. Deployment runbook: `docs/do-update.md`. Release history: `CHANGELOG.md`.
- **Stack:** MySqlConnector (no ORM), Polly (retry + circuit breaker), CsvHelper, Cronos, Serilog
  (JSON, 7-day rolling file), prometheus-net. Host: Generic Host + minimal ASP.NET Core.
- **Layout:** `Program.cs` (web mode + `--console` one-shot); `Processing/StationWorker.cs` (cron scheduler,
  parallel CA/US passes, `RunOnStartup`); `Sources/` (CA CSV fetcher + USGS Water Data API client); `Processing/` (station
  processors; `UsgsParameterMap` keeps the legacy USGS names the proc expects); `Data/` (repositories calling the
  envdata procs `sp_water_save_ca`, `sp_water_save_us`, `sp_station_503_*`, `sp_job_run_record`,
  `sp_clean_old_water_data`); `Configuration/` (options, `.env` loader, `MySqlUrl`, Polly pipelines); `Web/`
  (`/health`, metrics, DB check).
- **Endpoints:** `/health` on **8080** (public probe); metrics + liveness/readiness on **8081** (private,
  never publish).

## Build & test

```bash
dotnet build
dotnet run --project WaterService.Tests        # TUnit
```

**Test seams:** `WaterStationRepository`, `StationPostProcessingService`, `StationProcessorCA`/`US` and
`WaterMetrics` are **not `sealed`**, and the members `StationWorker` calls on them are `virtual`, so
`StationWorkerTests` can subclass them with `null!` internals instead of hitting a socket or a DB. That is
the whole reason those types are open — keep them that way, and prefer overriding to adding new interfaces
or DI indirection.

Docker: multi-stage build publishing a **self-contained linux-x64** app onto `debian:trixie-slim`
(GA .NET 10 has no Debian-trixie base image), non-root uid 10001. **Do not set `InvariantGlobalization`**
(platform rule; `libicu76` is installed in the image).

**Engine tests:** `WaterEngineTests` runs the real repositories against a real MySQL when `WATER_TEST_MYSQL` holds a
connection string of a THROWAWAY database built from `envfish-db/mysql/envdata`; otherwise its tests are no-ops.
Any change to SQL text must be run that way (a fake never parses SQL).

## Secrets

The envdata credentials come from **`MYSQL_URL` / `MYSQL_USERNAME` / `MYSQL_PASSWORD`** (real environment variables,
or a local `.env` as a lowest-precedence fallback). `MYSQL_URL` is `jdbc:mysql://<host>:3306/envdata?sslMode=REQUIRED`
or a native MySqlConnector string (`Configuration/MySqlUrl.cs`). The MySQL account (`waterservice`) has EXECUTE on
`envdata` only and requires TLS. **`DB_URL` / `DB_USERNAME` / `DB_PASSWORD` stay in the same env file as the SQL
Server connection** (user rule 2026-11-25: the file carries both); 11.0.0 does not open it, 10.x did, so a rollback
needs no file change. **Never commit real credentials** — committed
files and `docs/*` use placeholders only. On the droplet the secret is a volume-mounted file read via
`DOTENV_PATH`.

Any of those values may be stored as **`enc:v1:` AES-256-GCM ciphertext** instead of plaintext
(`Configuration/SecretCodec.cs`). Values without the marker are used verbatim, so a plaintext or
partially-encrypted file stays valid and this image runs against the existing all-plaintext `.env`.
The format is **wire-compatible with the Java services and `efj-backend/secret/Protect-Env.ps1`**, which
generates the deployable file — one encrypted `.env` serves both services, and `SecretCodecTests` pins
the interop against fixtures produced by that script. Decryption covers both delivery paths: values read
from the `DOTENV_PATH` file *and* encrypted values already injected as real environment variables (how
Docker's `--env-file` delivers them). The variable name is bound in as additional authenticated data, so
a ciphertext cannot be relocated from one key to another. A missing or wrong key is a **hard startup
failure**, never a silent pass-through — handing a raw `enc:v1:` string to SqlClient would surface as a
baffling login error. Key material comes from `FF_MASTER_KEY_FILE` (a path — preferred, keeps it out of
`docker inspect`) or `FF_MASTER_KEY`, as 32 bytes in hex or base64.
