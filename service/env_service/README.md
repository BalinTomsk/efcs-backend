# env_service

The water/weather service of FishFind (C# / .NET 10). It will serve every water, weather and forecast REST call from
the MySQL `envdata` database and hand the fish-model input over to SQL Server. Plan and status:
`docs/env_service_plan.md` (git-ignored; workstation only). Schema: `envfish-db/mysql/envdata`.

**Built so far:** `EnvService` 0.1.0 (plan step 4: station mirror + shadow hand-over) and `EnvService.Migrator`
(step 3, one-off). The REST API (`/api/v1/env/*`) comes with step 6.

## EnvService 0.1.0 — two background jobs

- **mirror** (startup, then hourly): SQL Server `env.fn_station_list()` → envdata `sp_station_mirror` (~11,700
  stations, `hasFish` included).
- **handover** (polls every 5 min; runs when a collector recorded a cycle in `job_run`, or at least hourly):
  `sp_fill_flow_state` + `sp_fill_pressure_state` in envdata, then `v_station_state` → `env.sp_push_station_state` and
  `v_station_day` → `env.sp_push_station_day` (1,000 rows per table-valued parameter), `env.sp_station_day_trim`, and
  finally `sp_job_run_handed_over`. **Shadow:** nothing in SQL Server reads `env.*` yet, and the fish procedures are
  not run here (plan step 5).

| Port | Paths |
|---|---|
| 8080 (public) | `/health` |
| 8081 (private) | `/health/live`, `/health/ready` (envdata + SQL Server), `/metrics`, `GET /admin/jobs`, `POST /admin/run/{mirror\|handover}` |

| Variable | Meaning |
|---|---|
| `MYSQL_URL` / `MYSQL_USERNAME` / `MYSQL_PASSWORD` | envdata (required) |
| `DB_URL` / `DB_USERNAME` / `DB_PASSWORD` | SQL Server, schema `env` (required) |
| `Env__MirrorIntervalMinutes`, `Env__HandOverPollSeconds`, `Env__HandOverMaxAgeMinutes`, `Env__PushBatchSize`, `Env__MirrorEnabled`, `Env__HandOverEnabled` | job settings (60, 300, 60, 1000, true, true) |
| `RABBITMQ_*`, `STATUS_MACHINE` | optional status-page heartbeats |

## EnvService.Migrator — the one-off copy of the SQL Server data

Copies, from SQL Server into `envdata`:

1. **stations** — every `WaterStation` row (with `hasFish` = its water body has species) through `sp_station_mirror`;
2. **water** — `WaterData` of the last `--days` (default 30) through `sp_water_backfill`, converted on the way:
   discharge `-999` → null, `pressure` > 800 = hPa → mm Hg (450..800 is already mm Hg; a high gauge reads 845..885 hPa), pH dropped (WaterData's US pH is
   truncated); a value already in `envdata` wins;
3. **weather** — `weather_Forecast` from `--days` back, one canonical `fishfind.weather.forecast/v1` envelope per station
   through `sp_weather_save` (pressure → mm Hg there); refused when `envdata` already holds weather, unless
   `--force-weather`;
4. **fills** — `sp_fill_water_reading_day`, `sp_fill_flow_state`, `sp_fill_pressure_state`.

It only READS SQL Server (`WITH (NOLOCK)`), and writes `envdata` only through procedures. Every step is idempotent.

```text
EnvService.Migrator [--env-file <plain KEY=VALUE file>] [--days 30] [--steps stations,water,weather,fills]
                    [--dry-run] [--force-weather] [--no-prompt]
```

| Variable | Meaning |
|---|---|
| `DB_URL` / `DB_USERNAME` / `DB_PASSWORD` | SQL Server (`jdbc:sqlserver://…` or a SqlClient string) — read only |
| `MYSQL_URL` / `MYSQL_USERNAME` / `MYSQL_PASSWORD` | envdata (`jdbc:mysql://…` or a MySqlConnector string) |

A missing password is asked for on the console (not echoed). `enc:v1:` values are refused: give plaintext.

## Build & test

```bash
dotnet build EnvService.slnx
dotnet run --project EnvService.Tests              # TUnit
dotnet run --project EnvService.Migrator.Tests     # TUnit
```

Release history: [`CHANGELOG.md`](CHANGELOG.md).
