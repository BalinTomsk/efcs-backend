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
- Whenever **this `claude.md`** is updated — apply the same change to `docs/specification.md`
  if it affects behaviour, structure, or configuration.
- Whenever behaviour changes, also add an entry to `CHANGELOG.md` under `## [Unreleased]`.
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
| Runtime target | Docker on Debian 13 (amd64), non-root uid 10001 |
| Env delivery | a mounted env file read via `DOTENV_PATH`; `enc:v1:` values need `FF_MASTER_KEY_FILE` |
| Logs | Serilog JSON to `/app/logs` (bind-mount a volume there), daily roll, 7-day retention |
| Published port | `8080` (health). `8081` (metrics/liveness/readiness) is **not** published |

> **Deployment specifics are deliberately not in this repo.** Host addresses, image coordinates, volume
> and secret paths live in `docs/do-update.md`, which is git-ignored and stays on the workstation. This
> repository is **public** — do not reintroduce them here, in `README.md`, or in `CHANGELOG.md`.

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
  build/run. Deployment runbook: `docs/do-update.md`. Release history: `CHANGELOG.md`.
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
dotnet run --project WaterService.Tests        # 57 TUnit tests
```

**Test seams:** `WaterStationRepository`, `StationPostProcessingService`, `StationProcessorCA`/`US` and
`WaterMetrics` are **not `sealed`**, and the members `StationWorker` calls on them are `virtual`, so
`StationWorkerTests` can subclass them with `null!` internals instead of hitting a socket or a DB. That is
the whole reason those types are open — keep them that way, and prefer overriding to adding new interfaces
or DI indirection.

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
