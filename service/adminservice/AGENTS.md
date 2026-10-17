# adminservice (C# / .NET 10) — Agent Context

Collects the **status of every backend service** for the admin page `fishfind-frontend/aspnet/stats/Default.aspx`.
Each service publishes `fishfind.service-status.v1` messages to the RabbitMQ queue `fishfind.servicestatus.events`;
adminservice takes them off the queue and stores one row per (service, machine) in `dbo.ServiceStatus` through
`dbo.sp_service_status_report`. It also writes its own row directly, so the page shows when the collector is down.

Built like `../waterapi` (host skeleton, port split, `DOTENV_PATH` + `enc:v1:` secrets, Serilog JSON, prometheus-net,
self-contained linux-x64 on `debian:trixie-slim`, uid 10001) with news_idx's RabbitMQ management-API client.

**Source of truth:** `docs/specification.md`. Session log: `docs/memory.md`. History: `CHANGELOG.md`.

## Rules

- **DO NOT COMMIT / PUSH / open-merge-close PRs without explicit user permission.**
- **Runs on csnode, once.** One consumer per queue; a second copy would only split the messages.
- **Keep docs in sync.** Every change is ① code, ② `docs/specification.md`; behaviour changes also get a
  `CHANGELOG.md` entry under `[Unreleased]` with the reason. Version numbers come from `AdminService/AdminService.csproj`.
- **The message contract is shared** with the reporters in waterservice, weather, waterapi, news_idx, news_search
  (`Status/ServiceStatusReporter.cs` in each), docapi (`status/ServiceStatusReporter.java`) and cproxy
  (`src/service_status.cpp`). Change it in all of them at once, or add a field as optional.
- **Only adminservice declares the queue** (`x-message-ttl` 10 min, `x-max-length` 10 000). Publishers never declare:
  a declare with different arguments fails with 406 PRECONDITION_FAILED.
- **No database access except `dbo.sp_service_status_report`.** Before any database change read
  `c:\envoinx\fishfind\envfish-db\AGENTS.md`.
- **Nothing is re-published.** A message that fails to store is dropped; the next heartbeat (one minute later) carries
  the same state. Do not add a retry queue.
- **Never crash-loop.** RabbitMQ unavailable = retry the declare with back-off; csnode is small and shared.
- Never publish 8080 or 8081. adminservice has no public API.

## Build & test

```bash
dotnet build AdminService.slnx
dotnet run --project AdminService.Tests        # TUnit
```

## Status

0.1.0 **built, not deployed** (2026-10-16). Verified end to end on the workstation: waterapi and docapi published
through a stand-in management API, adminservice stored the rows in CTRL envionx, and `fn_service_status_list` turned a
killed waterapi dead after 180 s.
