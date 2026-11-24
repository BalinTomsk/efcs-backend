# AGENTS.md — WeatherService (.NET port of weather-station-pusher)

## Project

C#/.NET 10 port of the Java `weather-station-pusher` at
`c:\envoinx\fishfind\efj-backend\service\weather`. Sibling of the `waterservice` port in this repo,
which it deliberately mirrors in structure, conventions, and infrastructure code.

**It is a clone, not a redesign.** Same database objects, same environment-variable contract, same HTTP
health paths, same log message wording. When the two diverge, the Java service is the reference and the
divergence must be documented here.

## Keeping docs in sync — IMPORTANT

`docs/specification.md` is the **single source of truth** used to recreate this service from scratch,
and `CHANGELOG.md` is the single source of truth for release notes. Whenever a source file, `.csproj`,
`appsettings.json`, or `Dockerfile` changes, update `docs/specification.md` to match. Treat every code
change as two steps: ① change the code, ② update the spec.

The operational docs are gitignored (`docs/`, `.claude/skills/`) and live only on this machine, so they
drift silently. When a change touches configuration, mounts, ports, or the deploy sequence, update all
four together:

| File | Covers |
|---|---|
| `docs/install.md` | Standing the service up on a **new** droplet |
| `docs/do-update.md` | Deploying a new tag to an existing droplet |
| `.claude/skills/update-weather/SKILL.md` | The automated version of `do-update.md` — keep the two in step |
| `.env.example` | The variable contract, mirrored in `efcs-backend/secret/plaintext.env` |

## Git

- **DO NOT COMMIT, PUSH, or CREATE/MERGE PULL REQUESTS without explicit user permission.** Make the
  edits, then stop with a status summary.

## Tech stack

| Key | Value |
|---|---|
| Service name (log `service` field) | `debian-weather` |
| Language | C# / .NET 10 (`net10.0`) |
| Projects | `WeatherService`, `WeatherService.Tests` (`WeatherService.slnx`) |
| Entry point | `WeatherService/Program.cs` (top-level statements) |
| SQL | `MySqlConnector` to MySQL `envdata` (since 11.0.0; no SQL Server), parameterised `CALL`s and view reads — no ORM |
| Resilience | Polly (retry + circuit breaker) + `System.Threading.RateLimiting` |
| Logging | Serilog, `RenderedCompactJsonFormatter`, console + 7-day rolling file |
| Mail | MailKit |
| Tests | TUnit |

## Deployment status

Live on the csnode droplet — host, container, and volume details are in `docs/do-update.md`
(git-ignored; this repository is public, so do not reintroduce them here).
The deployed version is the newest released heading in `CHANGELOG.md` — do not pin it here.
Three workers enabled (Weather.gov, Open-Meteo, Weather Canada); Visual Crossing and Google Weather are
off via `*_ENABLE=false` and have no API keys. The Java service still runs on `debian-jnode` against the
same table — see `docs/do-update.md` → Coexistence.

## Layout

```
WeatherService/
  Configuration/   options, .env + secret loading, JDBC URL translation, Polly pipelines, DI
  Domain/          StationRef
  Data/            connection factory, station + weather-data repositories
  Sources/         WeatherFetcherBase + the five provider fetchers, rate limiters
  Processing/      StationProcessorBase + five processors, StationWorker, usage tracker, post-processing
  Reporting/       cycle recorder, crash tracker, weekly report email
  Web/             AppInfo, DbHealthCheck
```

## Deliberate deviations from the Java service

Each of these is a considered choice, not drift. Anything not listed here should match Java behaviour.

1. **One `WeatherFetcherBase` instead of five near-identical fetchers.** The Java fetchers repeat the
   429/Retry-After loop, size cap, and shape guard verbatim in every file. Here that lives in the base
   and each provider supplies only its URL, headers, and message wording — the messages are composed to
   read *exactly* as the Java ones did, so log greps still work across both implementations.

2. **The rate limiter is not a Polly strategy.** Polly's rate-limiter queues on the execution's own
   cancellation token with no separate acquisition deadline, which loses Resilience4j's "wait up to
   `timeout-duration`, then refuse". `ProviderRateLimiter` wraps a `FixedWindowRateLimiter` and is
   awaited as the first step *inside* the pipeline's delegate, which also reproduces Resilience4j's
   Retry &gt; CircuitBreaker &gt; RateLimiter nesting.

3. **A permit-acquisition timeout throws `RateLimitPermitTimeoutException`, outside the `IOException`
   hierarchy.** That makes it neither retried nor counted against the breaker — matching Resilience4j,
   where `RequestNotPermitted` is not an `IOException` and so matches neither `retry-exceptions` nor
   `record-exceptions`.

4. **A failed cycle cools off for one minute before retrying.** The Java loop goes straight back around.
   Every failure at that level happens before the first station (the station COUNT query), so with the
   database down there is nothing to slow the loop — it spins at thousands of iterations a second across
   five workers, pinning a core and burying the log. Verified during the port.

5. **`RenderedCompactJsonFormatter`, not `CompactJsonFormatter`.** `ServiceLifecycleTracker` reads its
   own log files back to describe a crash; a bare message template with the values split into separate
   properties makes a useless incident summary. (`waterservice` uses the plain compact formatter because
   nothing reads its logs programmatically.)

6. **`EnvironmentAliases` replaces Spring's `${VAR:default}` placeholders.** .NET's configuration chain
   has no placeholder syntax, so the flat variable names are mapped onto section keys by an explicit
   table applied as a high-precedence source. Blank/absent variables are skipped so they cannot clobber
   an `appsettings.json` default with an empty string.

7. **Post-processing is serialised with a `SemaphoreSlim`** (Java used a `synchronized` method) — five
   workers finish cycles independently and these procedures rewrite shared aggregates.

8. **Per-provider `<PROVIDER>_ENABLE` toggles, and a blank API key skips a metered worker at startup**
   rather than starting it and failing every station. See the Gotchas entry below for why.

9. **Pacing is per-provider (`<PROVIDER>_TIMEOUT` seconds), derived from the daily limit over 12 hours
   when unset.** Java spread the *actual station count* over a fixed 8-hour budget, so the gap changed
   with however many stations happened to be loaded. Keying it to the provider's own quota instead makes
   the request rate predictable per provider and independent of the day's station count.

10. **Open-Meteo's daily User-Agent build numbers differ from Java's.** Same seed (the day number), but
   .NET's `Random` is a different generator. The property that matters — stable within a day, different
   across days — holds, and is asserted.

## Gotchas

- **Do NOT set `InvariantGlobalization`** (platform rule; Microsoft.Data.SqlClient, used up to 10.5.0, threw at
  connection open without it). The runtime image installs `libicu76`. (Same as `waterservice`.)

- **Crash detection depends on a graceful stop.** `ServiceLifecycleTracker` writes `RUNNING` at startup
  and `CLEAN` on shutdown; a next startup that still sees `RUNNING` reports a crash. `docker rm -f` and
  `docker kill` send SIGKILL, skip the shutdown hook, and therefore make every deploy look like a crash.
  Deploy tooling must use `docker stop` (SIGTERM) then `docker rm`.

- **Hosted-service registration order is load-bearing.** They stop in *reverse* order, so
  `ServiceLifecycleTracker` is registered first — its clean-shutdown marker is written only after the
  workers have actually stopped. Reordering silently breaks crash reporting.

- **`Weather:Lifecycle:LogFile` must match the Serilog file sink path in `Program.cs`** or a detected
  crash gets no description.

- **Two independent gates decide whether a worker starts** (`StationWorker.WorkerDisabledReason`):
  `<PROVIDER>_ENABLE=false` turns one off deliberately (all five default to `true`, so a provider is
  opted *out*), and a metered provider with a blank `VISUAL_CROSSING_API_KEY` /
  `GOOGLE_WEATHER_API_KEY` never starts either. The toggle is checked first, so a switched-off provider
  does not also nag about a key it will never use. Starting a keyed worker without its key would fail
  *every* station it touched, pushing the cycle's failure rate past `MaxFailureRate` and suppressing
  post-processing for a country whose other providers were healthy. The fetchers still refuse to call
  without a key as defence in depth. **Deviation from Java**, which starts all five unconditionally.

- **`<PROVIDER>_TIMEOUT` is honoured verbatim, including below the 2-second floor.** The floor exists
  only to stop a nonsensical *derived* value (a huge daily limit) becoming a burst. An explicit number is
  an operator decision, so it wins.

- **`mli` is a WATER gauge id, not a weather-station id.** `v_station_weather_due` comes from envdata `station`,
  the mirror of SQL Server's `WaterStation`; US rows are USGS site numbers. Any provider addressed *by station id*
  must first resolve the coordinate — `WeatherGovStationResolver` does this and caches the answer in envdata
  `weather_gov_station`. Coordinate-addressed providers (Open-Meteo, Visual Crossing, Google) need
  nothing. Weather.gov silently 404'd all 2,219 US stations until 10.0.1 because of this.

- **Provider coverage is flagged in envdata `provider_coverage`.** Not every provider answers every
  coordinate — SWOB is an observation network with real gaps, gridded providers have none. `StationWorker`
  records PROCESSED/SKIPPED per (gauge, provider); a FAILURE is deliberately not recorded, because it is
  transient and would otherwise exile a well-served gauge to the fallback worker. Query the gaps with
  `v_provider_coverage` (`WHERE provider = ? AND covered = 0`).

- **A 100%-skipped cycle looks healthy.** `Skipped` is deliberately not a failure, so a provider that
  matches *nothing* passes every health check, runs post-processing, and logs no errors. Startup
  verification does not catch it either — it uses a hard-coded known-good station. When adding or
  changing a provider, check the skip *rate*, not just that the smoke check passed.

- **The daily usage ledger charges per station, never up front.** Booking the whole daily limit at
  cycle start (the Java behaviour, shipped in 10.0.0) means any restart forfeits whatever the
  interrupted cycle had not used — on 2026-08-08 three restarts burned ~3,050 station-slots for ~154
  stations of work and idled the service until the next UTC day. Charge immediately before each fetch;
  that is also the only form that is correct after a hard kill, where nothing can credit anything back.
  An unwritable state directory still skips the provider entirely rather than running unmetered.

- **No raw table access.** Since 11.0.0 the grandfathered `UPDATE dbo.ows_meteo` is gone: payloads go through
  `sp_weather_save`. Every statement is a `CALL` of an envdata procedure or a read of an envdata view.

- **The payload is stored as sent** (`weather_raw`, newest per station) and only the canonical envelope is
  parsed, by `sp_weather_save`. The response size cap and the cheap `{`-first shape guard exist because the body
  goes straight into a column: an unbounded read is an unbounded INSERT, and an HTML error page returned with a
  200 would be stored as the station's weather.

- **Engine tests:** `WeatherEngineTests` runs the real repositories against a real MySQL when `WEATHER_TEST_MYSQL`
  holds a connection string of a THROWAWAY database built from `envfish-db/mysql/envdata`; otherwise they are no-ops.
  Any change to SQL text must be run that way (a fake never parses SQL).

- **`StationProcessorOpen.Country` is `"US"` even though the Open-Meteo worker serves CA.** Inherited
  from Java; the worker passes the real country per call, and the processor's own value is only a
  fallback for the single-argument overload.

## Database

Follows the schema in the separate `envfish-db` repo. **Before making ANY database change, read
`c:\envoinx\fishfind\envfish-db\AGENTS.md` first** — it is the authoritative DB workflow (never edit the
generated `ffi2.sql`; edit the `scriptNN_*.sql` sources; test-first: a FAILING unit test to confirm the
bug, then a PASSING one to verify the fix). That file does not auto-load here, so it must be opened explicitly.
This service's database is the MySQL **`envdata`** schema in `envfish-db/mysql/envdata` (tests in its
`UNIT_TESTS`; AGENTS.md section "MySQL envdata").

Objects used (all envdata): `sp_station_weather_due`, `v_station_weather_due` (read), `sp_weather_save`,
`v_weather_gov_station`, `sp_weather_gov_station_save`, `sp_provider_coverage_save`, `sp_job_run_record`,
`sp_clean_old_weather_data`. The account (`weather`) has EXECUTE on `envdata` only.
