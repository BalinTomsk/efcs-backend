# Changelog

All notable changes to `adminservice`. Group entries under `Added` / `Changed` / `Fixed` / `Removed` / `Security`,
and say **why**, not only what. The specification describes the current state; this file carries the history.

## [Unreleased]

## [0.1.1] — 2026-10-17 (not deployed)

### Changed
- **`DOTNET_TieredPGO=0` in the image** (was 1). csnode is a 1 GB droplet running five .NET services with about
  125 MB available; PGO keeps extra instrumented code versions. Measured on waterapi with 1 CPU: about 15 MB less per
  container. Not a leak fix -- csnode memory was flat over a 4-minute per-process sample; it is simply full.

## [0.1.0] — 2026-10-16 (not deployed)

### Added
- **Service status collector.** Polls `fishfind.servicestatus.events` (RabbitMQ management HTTP API, the platform's
  usual transport) and stores each `fishfind.service-status.v1` message in `dbo.ServiceStatus` via
  `dbo.sp_service_status_report`, for the admin page `stats/Default.aspx`. The point is one place that knows which
  backend processes are alive, on which machine, at which version, and what each last did — without giving every
  service its own MSSQL connection (cproxy has no SQL client at all).
- Per batch only the newest message per (service, machine) is stored; invalid messages are dropped with a warning.
- Declares the queue with a 10-minute message TTL and a 10 000-message cap, so an outage of adminservice leaves no
  backlog: a heartbeat is worthless once the next one is due.
- Writes its own row (`adminservice`) every 60 s directly, so the page also shows when the collector is down.
