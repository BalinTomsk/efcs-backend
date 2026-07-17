# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

OWMService is a Windows Service (C# 8.0 / .NET Framework 4.7.2) in the FishFind backend. Once per day it pulls 5-day weather forecasts for ~2,400 water-monitoring stations from two external APIs, stores the raw JSON in SQL Server, and lets database triggers/stored procedures parse it and recompute fish-catch probabilities. It is one of three backend services (the others — `water-station-pusher` and `auth-service` — are Java/Spring Boot and live elsewhere in `efcs-backend`).

## Build & test

This repo has an unusual project setup — read carefully before building:

- **`OWMService.csproj`** (repo root) is the real, buildable project. It is an **old-style MSBuild project** using `packages.config` (NOT SDK-style). Despite the `<Compile>` layout it pulls in *everything*: root files (`Program.cs`, `RWS.cs`), the `Config/` and `Logging/` folders, the `OWMService/Workers` + `OWMService/Logging` subfolders, **and** the test file `OWMService.Tests/Workers/WeatherDataWorkerTests.cs` (compiled directly into the exe, along with xUnit/Moq references).
- **`OWMService.sln`** references only `OWMService.csproj`.
- **`OWMService.Tests/OWMService.Tests.csproj`** is a separate SDK-style test project that is **not** in the solution. It duplicates the same test file via `ProjectReference`.

Because the main project is packages.config-based, build with MSBuild + NuGet restore, not `dotnet build`:

```powershell
nuget restore OWMService.sln          # or: msbuild -t:restore
msbuild OWMService.sln /p:Configuration=Release   # output: bin\Release\OWMService.exe
```

Or just open `OWMService.sln` in Visual Studio 2022 and Build Solution.

Run tests via the SDK test project (the one meant for `dotnet`):

```powershell
dotnet test OWMService.Tests/OWMService.Tests.csproj
dotnet test OWMService.Tests/OWMService.Tests.csproj --filter "FullyQualifiedName~Process_WithInvalidConnection"   # single test
```

Tests use xUnit + Moq. They mock `IEventLogger` and exercise `Process(...)` guard clauses (empty/invalid connection strings, null-logger throws) — they do **not** hit real APIs or a database.

## Run / debug

`Program.cs` branches on `Environment.UserInteractive || Debugger.IsAttached`:
- **Console/debug mode** (F5 in VS, or running the exe from a terminal): runs both workers **once** immediately, then waits for Enter. This is the way to test a full cycle locally.
- **Service mode** (launched by the Windows SCM): starts the daily timer loop.

Install/uninstall the service (Command Prompt as Administrator):
```
sc create OWMService binPath= "C:\Path\To\OWMService.exe"
sc start OWMService
sc stop OWMService && sc delete OWMService
```

## Configuration (no config file for secrets)

Runtime settings (SQL creds, API key) come from the **registry**, read by `RegistrySettingsProvider`:
`HKEY_LOCAL_MACHINE\SOFTWARE\FishFind\OWMService` (64-bit view) — values `Server`, `dbName`, `userName`, `userPassword`, `wunderground` (Weather.com API key), `Interval`. Import template `Res\OWMService.reg` and edit. If `Server`/`dbName` are missing, `OnStart` logs and bails.

`App.config` holds only non-secret logging settings: `LogFilePath`, `EventLogSource`, `EventLogName`. Default log path: `C:\ProgramData\OWMService\Logs\OWMService.log`.

Defaults hardcoded in `Config/Settings.cs` (superadmin/superpassword/placeholder key) are fallbacks only — registry values override them.

## Architecture

**Daily orchestration (`RWS.cs`)** — the `ServiceBase`. `OnStart` reads settings and starts a `System.Timers.Timer` (10s interval). `TimerElapsed` is guarded by the `m_bFlagProcessing` re-entrancy flag; each cycle it:
1. Instantiates fresh `WeatherDataWorkerWg` then `WeatherDataWorkerOpen` (worker fields are nulled after each cycle so DI overrides survive but default instances are recreated daily).
2. Runs each worker with an **8-hour `WorkerTimeBudget`**. The Open-Meteo worker always runs even if the Wunderground worker failed.
3. Calls `WaitUntilNextDay()` — a blocking `Thread.Sleep` until midnight. On an unhandled exception it sleeps 1 hour instead (avoids a tight retry loop). So effectively one full cycle per calendar day.

**Worker hierarchy (`OWMService/Workers/`)** — Template Method pattern.
- `IWeatherDataWorker` → `bool Process(Settings, TimeSpan budget)`.
- `WeatherDataWorkerBase` holds all the real logic; subclasses only override the source-specific bits: `GetServiceName`, `GetStationQuery`, `GetStationMaxLimitPerDay`, `GetSourceType`, `GetApiUrl`.
- `WeatherDataWorkerWg` — Weather.com/Wunderground (needs API key), `country = 'CA'`, type `1`, cap 1000.
- `WeatherDataWorkerOpen` — Open-Meteo (free, no key), `country = 'US'`, type `2`, cap 1400.
- Note: the two workers are split by **country** (`CA` vs `US`) via the `dbo.vwWeatherForecastToDay` view — this is the source of truth, even though `readme.md` / `Docs/specification.md` describe older station-query variants.

**Per-cycle flow inside `Process`:** open one `SqlConnection` → load stations via `GetStationQuery()` → compute per-station delay so the whole batch fits the time budget (min 2s each) → for each station fetch JSON and `UPDATE ows_meteo SET type, ows, stamp WHERE mli = @mli` → after all stations, run `ProcessFishState` (exec `spPushSpeciesFromLakeToStation` then `spTotalUpdateProbability`).

**The DB does the parsing, not C#.** The service only writes raw JSON into `ows_meteo`. The `UPDATE` fires trigger `TR_ows_meteo` → `sp_ows_meteo`, which parses the JSON (SQL Server `JSON_QUERY` / `STRING_SPLIT`) and MERGEs into `weather_Forecast`. See `Res\OWMService.sql` for the schema, trigger, and proc. When changing what the service stores or how forecasts are shaped, the change is usually in SQL, not C#.

**401 handling is special.** A `401` from a weather API surfaces as `UnauthorizedAccessException` from `ReadJSONOWSData`; `ProcessEnvData` catches it, stops that worker early, skips `ProcessFishState`, and returns `false` — but the *other* worker still runs. Any other HTTP/network error just skips that one station. A failed `ows_meteo` UPDATE dumps the payload to `Logs\failed_{mli}_{timestamp}.json`.

**Logging (`Logging/` + `OWMService/Logging/`).** `IEventLogger` with implementations `FileEventLogger` (default; all levels to file, errors also to Windows Event Log), `EventLogLogger`, `ConsoleEventLogger`. Always obtain one via `LoggerFactory` (currently returns `FileEventLogger` in both Debug and Release). Every class takes `IEventLogger` by constructor injection and throws `ArgumentNullException` on null — preserve this DI pattern when adding components.

## Conventions

- Private fields use `m_` prefix; statics use `s_`. Match this.
- Keep secrets in the registry, never in `App.config` or committed defaults.
- The `../secret/` folder (outside this project dir) is untracked — do not commit it.
