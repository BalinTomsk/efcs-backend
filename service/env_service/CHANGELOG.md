# Changelog — env_service

All notable changes go in this file, newest first, with the reason. Format:
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Each project's version is its csproj `<Version>`.

## [Unreleased]

### Added
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
