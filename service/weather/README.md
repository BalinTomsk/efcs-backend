# weather-station-pusher (.NET)

C#/.NET 10 port of the Java `weather-station-pusher` in
`efj-backend/service/weather`. Same database, same environment variables, same HTTP health paths, same
log field names — it is a drop-in replacement, not a reimagining.

## What it does

Five independent workers, one per provider/country pairing, each running its own daily cycle:

| Provider | Country | Toggle | Required key | Startup check station |
|---|---|---|---|---|
| Weather.gov | US | `WEATHER_GOV_ENABLE` | — | `KNYC` |
| Open-Meteo | CA | `OPEN_METEO_ENABLE` | — | `STARTUP-OPEN-CA` |
| Visual Crossing | US | `VISUAL_CROSSING_ENABLE` | `VISUAL_CROSSING_API_KEY` | `STARTUP-VISUAL-US` |
| Google Weather | US | `GOOGLE_WEATHER_ENABLE` | `GOOGLE_WEATHER_API_KEY` | `STARTUP-GOOGLE-US` |
| Weather Canada (SWOB) | CA | `WEATHER_CANADA_ENABLE` | — | `STARTUP-WEATHER-CANADA-CA` |

A worker starts only when its toggle is true **and** (if metered) its key is set; the toggles all default
to true, so a provider is opted *out* explicitly. A skipped worker logs
`Weather worker not started. provider=… reason=…` naming the variable to change, and the others carry on.
Running a metered worker without its key would fail every station it touched, dragging the cycle's
failure rate past the threshold and suppressing post-processing for a country whose other providers were
fine.

Each cycle reserves its slice of that provider's daily API budget, reads stations from
`dbo.vwWeatherForecastToDay`, fetches each station's payload, and stores it **verbatim** into
`dbo.ows_meteo` with `type = 2` — nothing parses the JSON.

Requests are paced per provider: `<PROVIDER>_TIMEOUT` seconds between calls, or — when that is `0` or
absent — that provider's `<PROVIDER>_DAILY_LIMIT` spread evenly over 12 hours.
 

## Run

```bash
dotnet run --project WeatherService/WeatherService.csproj
```

One-shot pass over US stations, then exit (exit code 1 only if every attempted station failed):

```bash
dotnet run --project WeatherService/WeatherService.csproj -- --console --station=KNYC
```

Tests:

```bash
dotnet run --project WeatherService.Tests/WeatherService.Tests.csproj
```

## Health

Port **8081** only — the same paths Spring Actuator served, so existing probes work unchanged.

| Path | Meaning |
|---|---|
| `/actuator/health` | `{ status, version, uptime }` |
| `/actuator/health/liveness` | Process only. Never DB-dependent, so an outage does not restart the container. |
| `/actuator/health/readiness` | Datasource reachable. |

## Configuration

Copy `.env.example` to `.env`, or inject the same variables as real environment variables (they win).
`DB_URL` accepts either the shared `jdbc:sqlserver://…` URL or a native SqlClient connection string.
 .

Everything else is in `WeatherService/appsettings.json` under `Weather:*` and `Smtp:*`;
`Configuration/EnvironmentAliases.cs` maps the Java service's flat variable names onto those keys.

## State directory

`Weather:Lifecycle:StateDir` (default `/app/logs/.lifecycle`) holds the crash marker, the incident log,
and the daily API-usage ledger. **Mount a volume at `/app/logs`** — otherwise crash history and quota
accounting reset every time the container is recreated, and a restart loop can re-spend a paid daily
budget.

Crash detection depends on the container being stopped **gracefully** (`docker stop`, i.e. SIGTERM).
`docker rm -f` / `docker kill` send SIGKILL, which skips the shutdown hook and makes every deploy look
like a crash.

 