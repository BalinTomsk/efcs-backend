# waterapi (C# / .NET 10) — Claude Context

Read-only HTTP API serving **cached water-station data to the FishFind frontend**. First controller:
**Station** — the map pins behind `fishfind-frontend/aspnet/Editor/ViewMap.aspx` (`dbo.vMapView`), served
from an in-process cache that a background refresher reloads every 15 minutes.

- Organised like `efj-backend/service/docapi` (controller → service → repository, `{data,error,meta}`
  envelope, exception → status mapping, `/health`, a no-DB default backing).
- Built and operated like its C# siblings on the same droplet (`../waterservice`, `../weather`): .NET 10,
  Microsoft.Data.SqlClient (no ORM), Polly, Serilog JSON, prometheus-net, `DOTENV_PATH` + `enc:v1:`
  secrets, self-contained linux-x64 on `debian:trixie-slim`, uid 10001.

**Source of truth:** `docs/specification.md`. Session log: `docs/memory.md`. History: `CHANGELOG.md`.
Deploy runbook: `docs/do-update.md` (git-ignored — host addresses stay off this public repo).

## Rules

- **DO NOT COMMIT / PUSH / open-merge-close PRs without explicit user permission.**
- **Keep docs in sync.** Every source change is ① code, ② `docs/specification.md`; behaviour changes also
  get a `CHANGELOG.md` entry under `[Unreleased]` (with the reason). Any change to an endpoint's contract
  also updates `docs/api-reference.html`, in the same change.
- **Version numbers come from `WaterApi/WaterApi.csproj`**, never copied from another doc.
- **Before any database change** read `c:\envoinx\fishfind\envfish-db\AGENTS.md` (edit `scriptNN_*.sql`,
  never `ffi2.sql`; test-first). waterapi created **no** SQL objects — it reads `dbo.vMapView` as is; what
  gets plotted is the view's decision, not this service's.
- **No per-request DB access.** Requests are answered from `StationMapCache`; only the cache (refresher or
  a cold first load) touches the database.
- **Keep the last good snapshot.** A failed refresh must never empty the map; only a country that has
  never loaded may answer 503.
- **Validate before the cache.** Bad `country`/`state`/`sid` is a 400 before anything is queried.
- Startup lines stay at `Information` (`waterapi … starting. storage=…`, `Station map refresher
  started…`, `Station map cache loaded…`) — they are how a deploy is verified without Debug.
- Do not set `InvariantGlobalization` (SqlClient needs ICU).
- The options class's `Countries` default must stay an **empty** array — the config binder appends to a
  non-empty default (it once produced `CA,US,CA,US`).
- Give variables, functions, modules, etc. names that unambiguously indicate their purpose
- Use ASD-STE100 for instructions and answers


## Build & test

```bash
dotnet build WaterApi.slnx
dotnet run --project WaterApi.Tests        # TUnit
```

## Deployed (2026-09-24)

waterapi **0.1.1** runs on csnode (resized to about 1 GB after the 464 MB droplet hung on the first
deploy), with `--memory 256m --memory-swap 256m --cpus 0.5`. It is bound to localhost and the csnode VPC
address on 8090, and is reached through cproxy 0.18.0 over VPC peering (nyc1 ↔ tor1). Details are in
`docs/memory.md` and the git-ignored `docs/do-update.md`.

- **Split public/management by `Connection.LocalPort`, never `RequireHost`.** The Host header carries the
  host-side port (8090) and matched nothing. That was the 0.1.1 fix.
- **Size the droplet before adding a process.** Check `free -m` and the neighbours' `docker stats`. Deploy
  with a memory cap and a guard that removes the container if available memory falls under 150 MB.

## Ports

`8080` public — `/api/v1/water/station/*` + `/health`. `8081` private — `/health/live`, `/health/ready`,
`/metrics`; never publish it. On the droplet host ports 8080 (water) and 8081 (weather) are already taken:
publish waterapi as `8090:8080`.

## Exposure — through cproxy (decided 2026-09-23)

The frontend reaches waterapi **only through cproxy**, exactly like docapi. cproxy 0.18.0 routes
`/api/v1/water/*` to `CPROXY_WATERAPI_UPSTREAM`, with the same guards and `X-Fish-Role`, and a circuit
breaker of its own. That is why every route here lives under **`/api/v1/water/`**: docapi already owns
`/api/v1/station/{id}`. **Keep new controllers under `/api/v1/water/`**, or cproxy sends them to docapi.

- Never publish waterapi to the internet directly. Bind it where only cproxy can reach it: the VPC address
  (csnode ↔ cproxy VPCs are peered, nyc1 ↔ tor1). Never a public port.
- waterapi compresses responses and answers ETag 304s. cproxy 0.18.0 had to be fixed for both, so it
  relays compressed bodies untouched and returns a 304 at once. Do not put waterapi behind an older cproxy.

## Open decisions

- **Frontend switch:** `Editor/ViewMap.aspx.cs` still queries `vMapView` directly (with `state`
  concatenated into SQL). Moving it to `/api/v1/water/station/map` through the gateway is a separate
  fishfind-frontend change.
