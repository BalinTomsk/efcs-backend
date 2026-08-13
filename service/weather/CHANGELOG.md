# Changelog

All notable changes for this service must be recorded in this file.

## Unreleased

- **CA daily limits raised to 2,300** (`OpenMeteo`, `WeatherCanada`) so one day's work reaches every
  Canadian station instead of rotating a slice. Below the eligible count the view's `ORDER BY NEWID()`
  just shuffles which stations get weather, and the rest wait days.

  Two things were wrong, not one. `WorkerOptions.DailyLimitOptions.OpenMeteo` still read `1400` even
  though its own XML comment already described the raise to 2,300 — the comment was written, the value
  was not. And `appsettings.json` pinned **both** CA providers at the old numbers (`OpenMeteo` 1400,
  `WeatherCanada` 900), which overrides the class defaults, so the earlier `WeatherCanada` raise had no
  runtime effect at all. Both files now agree at 2,300, as do `.env.example` and the specification.

  A generous cap costs nothing: the worker requests `Math.Min(totalSupportedStations, dailyLimit)`, so
  it never fetches more stations than exist, and both providers are free public feeds. This makes
  `StationWorkerTests.CaProviders_DailyLimitCoversEveryCanadianStation` pass — it had been failing on
  main.

- **Weather payloads are now converted to a canonical envelope before they are stored**, instead of the
  raw provider document being parsed by T-SQL inside a database trigger. Each provider gets an
  `IForecastConverter` (`Canonical/`) producing `fishfind.weather.forecast/v1` — see
  `docs/specification.md` §8a.

  Why: parsing lived in `dbo.TR_ows_meteo`, which **cannot raise** — an error there aborts this
  service's `UPDATE` and discards the payload just fetched — so a document no parser understood
  produced no rows and no error. A whole provider (Visual Crossing, ~230 US stations) went unnoticed
  that way. A converter runs here, so it **throws** and the station is counted as failed. Adding a
  provider now needs no database change.

  Converters: `OpenMeteoConverter` (already metric; performs the hourly→daily reduction the database
  used to do) and `VisualCrossingConverter` (°F→°C, mph→km/h, inches→mm; clips to today..today+6;
  splits daily rainfall evenly). Both reproduce the T-SQL arithmetic exactly so station numbers do not
  shift during the rollout, and their expectations match the Java service's converter tests.

- **`ows_meteo.type` now records which provider served each station.** It was hardcoded to `2` for
  every provider. `WeatherDataRepository.SaveStationDataAsync` takes the type and callers pass their own
  `WeatherSourceType`: Open-Meteo 2, Visual Crossing 4, weather.gov 5, Environment Canada 6,
  Weather Underground 7, Google Weather 8.

  Requires the matching database change (`sp_ows_meteo_canonical` + `TR_ows_meteo` envelope routing),
  already applied to prod. The legacy per-provider branches remain, so this service and the Java port
  can be deployed independently and in any order.

## [10.1.1] - 2026-08-11

**Fix: the weekly report's cycle-history buffer had gone stale relative to the real worker count.**

`CycleReportRecorder`'s capacity was `MaxEntriesPerWorker(7) * ExpectedWorkerCount`, but
`ExpectedWorkerCount` was a hardcoded `2` — set when the service ran two workers and never updated
as providers were added. With today's 4 active workers (Weather.gov, Open-Meteo, Weather Canada,
Wunderground) the 14-entry cap held only ~3.5 days, not the intended week; the Friday email would
silently be missing Monday–Wednesday's cycles despite being titled "Weekly Report."

`MaxEntries` is now derived from `StationWorker.WorkerCount` (the actual `Workers` array length)
instead of a hand-maintained constant, so it can't drift out of sync again as providers are added
or removed. Pure sizing fix — the report's own text was already honest about how many entries it
was showing, so no user-facing wording changed.

114 tests, all passing (the 1 pre-existing unrelated `CaProviders_DailyLimitCoversEveryCanadianStation`
gap is untouched by this change). **Deployed to prod 2026-08-11.**

## [10.1.0] - 2026-08-11

**Add Wunderground as a 6th weather-data provider (450 stations/day), C# port of the equivalent
Java change.**

A PWS Contributor key has no lat/lon forecast endpoint, so each station costs two calls:
`v3/location/near` resolves the nearest personal weather station to the water station's
coordinates, then `v2/pws/observations/current` fetches its latest reading — both run through the
same resilience pipeline/rate limiter as independent requests, so effective call volume against
the Wunderground quota is roughly double the configured daily station limit.

New `WundergroundFetcher` + `StationProcessorWunderground`, a `wunderground` `WorkerDefinition`
(US), and config (`WundergroundApiKey`, `Enable.Wunderground`, `Timeout.Wunderground`,
`DailyLimit.Wunderground` = 450), mirroring the existing Google Weather pattern exactly. 113 tests
(1 pre-existing unrelated failure, see repo history).

**Deployed to prod 2026-08-11** alongside the Java service's identical change — both run at the
full 450/day against the same Wunderground account by deliberate choice (unlike Visual
Crossing/Google Weather, which stay Java-only to avoid double-spending a shared metered quota).

## [10.0.3] - 2026-08-08

**Flag the gauges a provider cannot serve, so a different worker can pick them up.**

Weather Canada's SWOB is an observation network with genuine geographic gaps — even at a 0.5°
(~55 km) search box, roughly one Canadian gauge in six has no nearby site. Only gridded providers
(Open-Meteo, Visual Crossing, Google) can answer an arbitrary coordinate. Those gauges previously
skipped silently on every cycle forever, and since a fully-skipped cycle still reports healthy,
nothing ever surfaced them.

DB (envfish-db): `dbo.weather_station_coverage` — one row per (gauge, provider), plus
`dbo.fn_weather_station_coverage`, `dbo.fn_weather_uncovered_stations` and
`dbo.sp_save_weather_station_coverage`. `fn_weather_uncovered_stations(@provider)` returns the
gaps **with coordinate, state and country**, which is everything a fallback worker needs to fetch
them elsewhere. Covered by `unit_test@WeatherCoverage.sql` (5 tests, confirmed FAILing first).

Service: `StationWorker` records the flag as each station completes. **Only PROCESSED and SKIPPED
are coverage facts** — a failure is transient, and treating a timeout or a 503 as "not covered"
would route a perfectly-served gauge to the fallback worker on the strength of one bad night. The
write can never fail a station: the payload is already saved by then.

The flag is a current fact, not a log — one row per (gauge, provider), updated in place, so a gap
that later resolves (a new SWOB site, a widened box) simply clears.

107 tests, all passing. **Deployed to prod 2026-08-08**, DB objects first; coverage rows confirmed
being written for all three running providers.

## [10.0.2] - 2026-08-08

**Fix: the daily API allowance was booked up front, so any restart forfeited the rest of the day.**

`WeatherApiUsageTracker` charged the whole `<PROVIDER>_DAILY_LIMIT` at cycle start, before a single
station was fetched. Observed on 2026-08-08: the first cycle booked 900/900/1400 at 16:04:49, did
~154 stations of real work, and three restarts later the service had ~3,050 station-slots spent on
nothing and sat idle until the next UTC day.

- Budget is now charged **one station at a time, immediately before that station is fetched**
  (`TryConsumeAsync`). An interrupted cycle costs exactly what it used — and that stays true after a
  hard kill, where nothing gets a chance to credit anything back. Crediting the remainder on graceful
  shutdown would have patched the deploy case only.
- `SnapshotAsync` replaces the up-front reservation for sizing the cycle and for the budget log line,
  which now reads `usedToday=… remainingToday=…`.
- The ledger keeps **one aggregated row per (date, provider)**, incremented in place, so the file
  stays a few lines long however many stations a day runs. The old append-per-reservation format
  still reads correctly (entries are summed).
- Running out of allowance mid-pass is treated as a normal end of the day's work, not a fault:
  post-processing still runs for the stations that did complete.

Inherited from the Java service, which books the same way and has the same exposure.
106 C# tests, all passing.

**Deployed to prod 2026-08-08.** Today's ledger was cleared on deploy, since the recorded usage was
the old up-front artefact rather than real work. Verified live: budget starts `usedToday=0
remaining=900/1400/900 persisted=True`, the ledger increments one per station, and — with 10.0.1's
resolver — real gauges now resolve and fetch: `13213100 → KONO`, `04252500 → KRME`, zero Weather.gov
skips where previously every US station skipped.

## [10.0.1] - 2026-08-08

**Fix: every US station was being skipped, and no Canadian station resolved.**

Measured before the fix: Weather.gov 0/25 sampled stations returned data, Weather Canada 0/25.
Only Open-Meteo was landing anything. Invisible in monitoring because a fully-skipped cycle is
"healthy" by design — zero failures, post-processing runs, health green.

- **Weather.gov was asked by the wrong identifier.** `dbo.vwWeatherForecastToDay` is built from
  `dbo.WaterStation`, so `mli` is a WATER gauge id — all 2,219 US rows are numeric USGS site
  numbers, none is an NWS call sign. `/stations/{mli}/observations/latest` therefore 404s for
  every US station, permanently. The service now resolves the gauge's COORDINATE to a nearby NWS
  station via `/points/{lat},{lon}/stations` and fetches that station's observation. Measured
  25/25 resolve and 25/25 return data. Two API details this depends on: coordinates must be
  rounded to 4 decimal places, and `/points` answers with a 301 that must be followed.
- **The resolution is cached in the database** — new `dbo.weather_gov_station` +
  `dbo.fn_weather_gov_station` + `dbo.sp_save_weather_gov_station`, covered by
  `unit_test@WeatherGovStation.sql` (4 tests, confirmed FAILing before the objects existed, then
  PASSing; full suite clean). The mapping is geographic and permanent, so resolving inline every
  cycle would double the request count against a rate-limited public API. A "no station nearby"
  answer is cached as a NULL `station_id`, so a point that will never resolve is asked once.
- **Weather Canada bbox radius 0.05° → 0.5°.** 0.05° (~5.5 km) matched no SWOB site for any
  sampled station; 0.25° recovered 12/25 and 0.5° recovered 21/25. SWOB is a real observation
  network with real gaps, so it cannot cover every inland point — only the gridded providers can.

Inherited from the Java service, which has the same identifiers and bbox default and has been
skipping the same stations. 102 C# tests, all passing.

**Deployed to prod 2026-08-08.** DB objects applied first (one transaction), then image
`10.0.1`. Verified live: health 10.0.1, readiness Healthy, all three workers' smoke checks pass,
and the first resolution round-tripped through the new proc into `dbo.weather_gov_station`.

## [10.0.0] - 2026-08-08

Initial C#/.NET 10 port of the Java `weather-station-pusher`
(`efj-backend/service/weather`, Spring Boot 3.5.16 / Java 21).

**Deployed to prod 2026-08-08** — `debian-csnode` (137.184.218.128), image
`ghcr.io/balintomsk/weather-station-pusher-cs:10.0.0`, port 8081, state on `volume-env` at
`/mnt/volume_env/weatherservice`. Version numbering follows the C# port line (10.x) to keep it
distinct from the Java service's 1.x, matching `water-station-pusher-cs`. First-time droplet setup
is documented in `docs/install.md`.

Verified live: `/actuator/health` reports 10.0.0, readiness `Healthy` (which also proves the
`enc:v1:` DB credentials decrypted), three workers started and passed their smoke checks
(Weather.gov, Open-Meteo, Weather Canada), the two metered workers correctly skipped on
`*_ENABLE=false`, budget ledger `persisted=True`, no phantom crash incident, zero errors.

**The Java weather service was left running on `debian-jnode`** — both now write `dbo.ows_meteo`.
See `docs/do-update.md` → Coexistence; this is not the water services' redundancy arrangement.

Ported in full:

- Five provider workers (Weather.gov US, Open-Meteo CA, Visual Crossing US, Google Weather US,
  Weather Canada CA), each with its own startup verification, daily cycle, and eight-hour pacing budget.
- Per-provider daily API budgets, reserved up front and persisted so a restart cannot re-spend them.
- Raw payload persistence into `dbo.ows_meteo` (`type = 2`), verbatim, with a response size cap and a
  JSON-object shape guard.
- Health-gated post-processing: `spPushSpeciesFromLakeToStation` → `spTotalUpdateProbability` →
  `sp_clean_old_weather_data`, skipped when the cycle's failure rate exceeds the threshold.
- Resilience per provider: retry with exponential backoff and jitter, circuit breaker, rate limiter,
  and inline `Retry-After` handling for HTTP 429.
- Crash/unclean-restart tracking and the Friday weekly report email (cycle summaries + incidents).
- `.env` loading, `enc:v1:` secret decryption, and JDBC-URL translation shared with the other services.
- Health endpoints on port 8081 at the original Actuator paths.
- `--console [--station=<MLI>]` one-shot mode.

Deviations from the Java implementation are enumerated in `CLAUDE.md` → "Deliberate deviations". The two
behavioural ones:

- A failed cycle waits a minute before retrying; the Java loop spins at thousands of iterations a second
  while the database is unreachable.
- Each worker is gated by two independent switches, neither of which exists in the Java service:
  a per-provider `<PROVIDER>_ENABLE` toggle (default `true`, so a provider is opted *out* explicitly),
  and — for the metered providers — a non-blank `VISUAL_CROSSING_API_KEY` / `GOOGLE_WEATHER_API_KEY`.
  A gated-off worker is not started at all rather than started and left failing every station, which
  would push the cycle's failure rate past the threshold and suppress post-processing for a country
  whose other providers were healthy. Both API keys are currently blank in
  `efcs-backend/secret/plaintext.env`, so a deploy today runs three workers: Weather.gov (US),
  Open-Meteo (CA), Weather Canada (CA).
- Pacing is per provider: `<PROVIDER>_TIMEOUT` seconds between calls, or — when `0`/absent — that
  provider's `<PROVIDER>_DAILY_LIMIT` spread over 12 hours. Java instead divided a fixed 8-hour budget
  by however many stations happened to be loaded, so the request rate moved with the station count.

95 tests (TUnit), all passing.
