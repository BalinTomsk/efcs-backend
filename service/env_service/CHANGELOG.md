# Changelog — env_service

All notable changes go in this file, newest first, with the reason. Format:
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Each project's version is its csproj `<Version>`.

## [Unreleased]

### Added
- **EnvService 0.3.0** (2026-11-25, LIVE on csnode 2026-11-24 21:07 UTC): plan step 6a, the first public read API on port 8080, so docapi
  (and later mcp-env and the portal) can read water and weather without SQL. Station and water-body reads, all from
  MySQL `envdata` (new views `v_station_info`, `v_weather_day`):
  - `GET /api/v1/env/stations/{sid}`
  - `GET /api/v1/env/stations/{sid}/current` (newest values + flow state)
  - `GET /api/v1/env/stations/{sid}/weather?from=&to=` (daily, with the day's pressure state; at most 31 days)
  - `GET /api/v1/env/lakes/{lakeId}/stations`
  - `GET /api/v1/env/lakes/{lakeId}/weather?date=` (each station's current values and that day's weather; backs the
    river Weather tab and the catch-log weather fill)

  Metric, pressure in mm Hg, times UTC. Stations come from an in-memory cache of `v_station_info` (job `stations`, at
  startup and every 15 min; a failed refresh keeps the last snapshot; 503 until the first load), so no request scans a
  table. Per-request reads are primary-key ranges through their own Polly pipeline `sql-read` (one 250 ms retry, own
  breaker), so HTTP callers never wait for the jobs' 2 s retries and never trip the jobs' breaker. Validation comes
  first: a bad sid / lakeId / date / range is a 400 before any read. `/health/ready` also requires the cache;
  `POST /admin/run/stations` reloads it. Tests 27/27 (11 new). Verified end to end on a local envdata.
- **EnvService 0.2.0** (2026-11-25, LIVE on csnode 2026-11-24 20:33 UTC): plan step 5. After every hand-over the job runs the fish model
  on the env path: `env.sp_push_species`, then `env.sp_update_probability` (`fish_location.today` from `env.StationState`
  / `env.StationDay`). Both do nothing (-1, `probability=legacy` in the result line) until SQL Server's
  `env.Setting probability_source` is `env`; then the Java weather service's `dbo.spTotalUpdateProbability` returns at
  once. So the cut-over and its rollback are one UPDATE each, with no deploy. The job is no longer labelled shadow.
- **EnvService 0.1.0** (2026-11-25, built, not deployed): plan step 4. Two background jobs: **mirror** (SQL Server
  `env.fn_station_list()` → envdata `sp_station_mirror`, at startup and hourly) and **handover** in SHADOW mode (flow +
  pressure fills in envdata, then `v_station_state` / `v_station_day` pushed into SQL Server `env.StationState` /
  `env.StationDay` by table-valued parameter, trimmed, and the pending `job_run` cycles marked only after every push
  succeeded). The fish procedures are not run yet (the Java weather service still runs them until step 5). Ops on the
  private port: `/health/live`, `/health/ready` (both databases), `/metrics` (`env_job_runs_total`,
  `env_job_last_success_timestamp_seconds`), `GET /admin/jobs`, `POST /admin/run/{job}`. Verified end to end on local
  MySQL + SQL Server: 11,719 stations mirrored in 3.5 s, 10,030 station states pushed, a recorded cycle handed over and
  marked. Needs schema `env` in SQL Server (envfish-db, `unit_test@EnvHandOver.sql` 4/4).
- **EnvService.Migrator 0.1.0** (2026-11-25): the one-off copy of the SQL Server water/weather data into MySQL
  `envdata` (plan step 3), so `envdata` starts with the 30 days of readings the flow median needs and the current
  forecast window. Steps `stations` (`sp_station_mirror`, incl. `hasFish`), `water` (`sp_water_backfill`), `weather`
  (`sp_weather_save` with a canonical envelope per station), `fills`. Conversions pinned by `ConversionsTests`:
  `WaterData.pressure` holds mm Hg and hPa per station (> 800 = hPa: mm Hg never exceeds ~800, and a high gauge
  reports 845..885 hPa station pressure), `-999` = no reading, WaterData's
  truncated US pH is not copied. Reads SQL Server only; writes `envdata` only through procedures; idempotent.
