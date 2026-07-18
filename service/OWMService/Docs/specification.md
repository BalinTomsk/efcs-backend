# OWMService — Full Application Specification

> **Purpose**: This document contains everything needed to recreate the OWMService application from scratch.

---

## 1. Overview

**OWMService** is a Windows Service (.NET Framework 4.7.2, C# 8.0) that runs as a daily background process. It fetches weather forecast data from two external APIs for water-monitoring stations stored in a SQL Server database, saves the raw JSON, and triggers server-side stored procedures that parse the weather data and update fish-catch probability scores.

### High-level data flow

---

## 2. Technology Stack

| Component | Technology |
|---|---|
| Runtime | .NET Framework 4.7.2 |
| Language | C# 8.0 |
| Service host | `System.ServiceProcess.ServiceBase` |
| Database | Microsoft SQL Server (via `System.Data.SqlClient`) |
| HTTP client | `HttpWebRequest` / `HttpWebResponse` |
| Test framework | xUnit + Moq |
| IDE | Visual Studio 2022 |
| Installer | `System.Configuration.Install` (`ProjectInstaller`) |

---

## 3. Project Structure

```plaintext
OWMService.sln
│
├───OWMService                     // Main service project
│   │   App.config                 // Application configuration file
│   │   OWMService.csproj          // Project file
│   │
│   ├───bin                        // Compiled binaries (output folder)
│   ├───obj                        // Intermediate object files
│   └───Properties                  // Assembly information and project properties
│
├───OWMService.Tests               // Unit test project
│   │   OWMService.Tests.csproj    // Project file
│   │
│   ├───bin                        // Compiled binaries (output folder)
│   ├───obj                        // Intermediate object files
│   └───Properties                  // Assembly information and project properties
│
└───Docs                           // Documentation
    │   specification.md           // This document
    │
    └───images                     // Images for documentation
```

---

## 4. Configuration

### 4.1 Registry Settings

Path: `HKEY_LOCAL_MACHINE\SOFTWARE\FishFind\OWMService`

| Value name | Type | Description |
|---|---|---|
| `Server` | REG_SZ | SQL Server hostname or IP |
| `dbName` | REG_SZ | Database name (default: `fishfind`) |
| `userName` | REG_SZ | SQL login |
| `userPassword` | REG_SZ | SQL password |
| `wunderground` | REG_SZ | Weather.com (Weather Underground) API key |
| `Interval` | REG_DWORD | Polling interval (currently unused — timer is fixed at 10s initial, then daily) |

### 4.2 App.config Settings

| Key | Default | Description |
|---|---|---|
| `LogFilePath` | `""` (empty) | Custom log file path. Empty = `C:\ProgramData\OWMService\Logs\OWMService.log` |
| `EventLogSource` | `OWMService` | Windows Event Log source name |
| `EventLogName` | `Application` | Windows Event Log name |

### 4.3 Connection String Format

Built using `SqlConnectionStringBuilder` to safely escape special characters in credentials:

```csharp
var builder = new SqlConnectionStringBuilder
{
    DataSource = Server,
    InitialCatalog = DbName,
    IntegratedSecurity = false,
    UserID = UserName,
    Password = UserPassword
};
```

Returns `string.Empty` if `Server` or `DbName` is null/whitespace.

---

## 5. Service Lifecycle

### 5.1 Entry Point (`Program.cs`)

1. Read `EventLogSource`, `EventLogName`, `LogFilePath` from `App.config`.
2. Create logger via `LoggerFactory.CreateDefaultLogger()`.
3. If `Environment.UserInteractive || Debugger.IsAttached` → **debug console mode**.
4. Otherwise → **Windows Service mode** via `ServiceBase.Run()`.

### 5.2 Service Class (`RWS.cs`)

- Service name: `OWMService`
- `CanStop = true`, `CanPauseAndContinue = true`
- Constructor chain supports DI: `(IEventLogger, ISettingsProvider, IWeatherDataWorker, IWeatherDataWorker)`

#### OnStart

1. Read settings from `RegistrySettingsProvider.TryReadSettings()`.
2. Apply non-blank values to `m_settings`.
3. Create `System.Timers.Timer` with 10-second interval, auto-reset, start.

#### OnStop / OnPause / OnShutdown

- Set `m_bFlagProcessing = true` to block timer callback.
- Stop and dispose timer.

#### TimerElapsed (main orchestration)

The two workers always run sequentially. If the Wg worker fails (e.g., 401), the Open worker still runs. After both complete (or fail), the service sleeps until midnight and the cycle repeats.

### 5.3 Debug Console Mode

- `RWS.StartDebug(args)` calls `OnStart`, then runs both workers once.
- `Console.ReadLine()` blocks until Enter.
- `RWS.StopDebug()` calls `OnStop`.

---

## 6. Worker Architecture

### 6.1 Interface

```csharp
public interface IWeatherDataWorker
{
    bool Process(Settings settings, TimeSpan timeBudget);
}
```

Returns `true` on success, `false` on auth failure or connection error.

### 6.2 Base Class (`WeatherDataWorkerBase`)

Abstract class providing the full processing pipeline:

#### Process(Settings, TimeSpan)

1. Build connection string via `SqlConnectionStringBuilder`. Return `false` if empty.
2. Open `SqlConnection`. Log `"Database connection opened."`.
3. `GetListOwsMeteo(cnn)` — execute `GetStationQuery()`, read `(mli, lat, lon, state)` into `List<StationData>`. Wrapped in dedicated try/catch that logs the failing SQL query text on error.
---

- `@type` comes from `GetSourceType()` (abstract).
- This UPDATE triggers `TR_ows_meteo` on the server, which calls `sp_ows_meteo` to parse JSON into `weather_Forecast`.

#### Static Configuration

```yaml
# see also: WeatherDataWorkerBase.Ctor
default:
  __meta:
    type: "default"
    desc: "Default configuration, applied to all instances."
  logging:
    level: "Information"
    fileSizeLimitBytes: 10485760
    maxAnnualLogFiles: 5
    enableConsole: false
  eventLog:
    source: "FishFind OWMService"
    log: "Application"
  sql:
    server: "localhost"
    database: "fishfind"
    user: "fishfind_rw"
    password: "tmppwd!234"
  weather:
    wunderground:
      enabled: true
      apiKey: "abc123xyz"
      interval: "00:10:00"
    openweather:
      enabled: true
      apiKey: "def456uvw"
      interval: "00:10:00"

development:
  logging:
    level: "Debug"
    enableConsole: true

production:
  sql:
    password: "<<Instance Specific>>"
  weather:
    wunderground:
      apiKey: "<<Instance Specific>>"
    openweather:
      apiKey: "<<Instance Specific>>"

```

### 6.3 Weather Underground Worker (`WeatherDataWorkerWg`)

| Property | Value |
|---|---|
| Source type | `1` |
| Station query | `SELECT TOP 1000 mli, lat, lon, state FROM dbo.vwWeatherForecastToDay WHERE sid % 2 = 1 ORDER BY stamp ASC` |
| API URL | `https://api.weather.com/v3/wx/forecast/daily/5day?geocode={lat},{lon}&format=json&units=e&language=en-US&apiKey={settings.Wunderground}` |

### 6.4 Open-Meteo Worker (`WeatherDataWorkerOpen`)

| Property | Value |
|---|---|
| Source type | `2` |
| Station query | `SELECT TOP 1400 mli, lat, lon, state FROM dbo.vwWeatherForecastToDay WHERE sid % 2 = 0 ORDER BY stamp ASC` |
| API URL | `https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&hourly=temperature_2m,relative_humidity_2m,precipitation_probability,pressure_msl,wind_speed_10m,wind_direction_10m,weather_code,rain&daily=temperature_2m_max,temperature_2m_min&timezone=auto` |

### 6.5 StationData Model

Defined in `WeatherDataWorkerWg.cs` (same namespace `OWMService.Workers`).

---

## 7. Timing Model

Each worker receives an **8-hour time budget**. The delay between station API calls is calculated dynamically:

Example calculations:

| Worker | Stations | Budget | Delay per station |
|---|---|---|---|
| Wg | 1000 | 8h (28,800,000ms) | 28,800ms (~28.8s) |
| Open | 1400 | 8h (28,800,000ms) | 20,571ms (~20.6s) |

After both workers finish, the service sleeps until midnight (00:00 next day).

### Ideal daily timeline

If a worker fails early, the next one starts immediately. The remaining time before midnight is absorbed by the sleep.

---

## 8. Error Handling

| Scenario | Behavior |
|---|---|
| Empty connection string | `Process()` returns `false` immediately |
| SQL connection failure | Caught in outer try/catch, logged, returns `false` |
| Station query failure | Caught in dedicated try/catch, logged with SQL query text, returns `false` |
| HTTP 401 Unauthorized | `UnauthorizedAccessException` thrown → station loop breaks → `Process()` returns `false`, skips `ProcessFishState` |
| HTTP non-200 (other) | Logged, returns empty JSON, station skipped |
| Network/timeout error | Logged, returns empty JSON, station skipped |
| Station processing exception | Logged with MLI and index, processing continues to next station |
| `SaveJSONOWSData` trigger failure | Logged, failed JSON payload saved to `C:\ProgramData\OWMService\Logs\failed_{mli}_{timestamp}.json`, station skipped |
| `ProcessFishState` exception | Logged, does not fail the entire process |
| Unexpected `TimerElapsed` exception | Logged, still waits until next day |

---

## 9. Logging

### Logger hierarchy

`LoggerFactory.CreateDefaultLogger()` always returns `FileEventLogger`.

### Log file location

- Custom: value of `LogFilePath` in `App.config`
- Default: `C:\ProgramData\OWMService\Logs\OWMService.log`

### Log format

---

## 10. Database Schema (Service-relevant)

### 10.1 `ows_meteo` — Weather JSON storage

```sql
CREATE TABLE [dbo].[ows_meteo] (
    [id] INT IDENTITY(1,1) PRIMARY KEY,
    [mli] VARCHAR(50) NOT NULL,
    [lat] FLOAT NOT NULL,
    [lon] FLOAT NOT NULL,
    [state] VARCHAR(10) NOT NULL,
    [timestamp] DATETIME NOT NULL DEFAULT GETDATE(),
    [json_data] NTEXT NOT NULL
);
```

Table `ows_meteo` is used to store raw weather JSON data fetched from external APIs.

- `id`: Unique identifier for each record
- `mli`: Monitoring Location Identifier (from FishFind)
- `lat`, `lon`: Latitude and Longitude of the monitoring station
- `state`: Additional state information (e.g. NV, CA)
- `timestamp`: Date and time when the record was created
- `json_data`: Raw JSON data as provided by the weather API

---

### 10.2 `TR_ows_meteo` — UPDATE trigger

On every `UPDATE` to `ows_meteo`, the trigger:

### 10.5 `vwWeatherForecastToDay` — Station selection view

Not directly defined in the codebase but referenced by both workers. Must return at minimum:

### 10.6 `spTotalUpdateProbability`

Updates fish catch probability in `fish_location` using:
- **Temperature** coefficient from `fn_get_koef_fish_station_temperature`
- **Oxygen** coefficient from `fn_get_koef_fish_station_oxygen`
- **pH** coefficient from `fn_get_koef_fish_station_ph`

Each coefficient uses a bell-curve model: values inside the optimal range → 1.0, tapering to 0.9 → 0.8 → 0.5 as conditions move away from optimal.

Also performs cleanup:
- Deletes `WaterData` older than 21 days
- Deletes `Weather_Forecast` older than 21 days

---

## 11. Service Installation

### 11.1 ProjectInstaller

### 11.2 Install Commands

```powershell
# Example commands to install/uninstall the service
# Install
InstallUtil.exe "C:\path\to\your\service\OWMService.exe"

# Uninstall
InstallUtil.exe /u "C:\path\to\your\service\OWMService.exe"

```

### 11.3 Registry Setup

Import `Res\OWMService.reg` or manually create:

````````
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SOFTWARE\FishFind\OWMService]
"Server"="."
"dbName"="fishfind"
"userName"="fishfind_ro"
"userPassword"="Guest123!"
"wunderground"="abc123xyz"
"Interval"=dword:00000000

````````

### 11.4 Database Setup

Run `Res\OWMService.sql` on the target SQL Server to create `ows_meteo`, `weather_Forecast`, `sp_ows_meteo`, and `TR_ows_meteo`.

---

## 12. Testing

### Framework

- **xUnit** for test runner
- **Moq** for mocking `IEventLogger`

### Test Coverage

| Test | What it validates |
|---|---|
| `Constructor_WithValidLogger_ShouldInitialize` | Worker instantiates with valid logger |
| `Constructor_WithNullLogger_ShouldThrowArgumentNullException` | Guard clause on null logger |
| `Process_WithEmptyConnectionString_ShouldReturnFalse` | Early exit on empty connection |
| `Process_WithNullConnectionString_ShouldReturnFalse` | Early exit on null values |
| `Process_WithInvalidConnection_ShouldReturnFalseAndLogError` | Connection failure logs error |
| `Process_WithVariousInvalidSettings_ShouldReturnFalse` | Theory: null, empty, whitespace |
| `Process_WithInvalidConnectionString_ShouldLogErrorMessage` | Verifies log message content |
| `Process_ShouldReturnResult` | Basic return value check |
| `WeatherDataWorker_ShouldImplementIWeatherDataWorker` | Interface implementation |

All `Process` tests pass `TimeSpan.FromHours(8)` as the time budget.

---

## 13. Coding Conventions

| Convention | Example |
|---|---|
| Member fields | `m_` prefix: `m_logger`, `m_timer`, `m_bFlagProcessing` |
| Static fields | `m_` prefix: `m_httpClient`, `m_escapeSequenceRegex` |
| Constants | `PascalCase`: `MinDelayBetweenStationsMs`, `EventSourceName` |
| Private const | `PascalCase`: `NullGuid` |
| Namespaces | Using-inside-namespace style for worker classes |
| Access modifiers | `protected` for base class methods meant for override/use in subclasses |
| Regions | Used in test files (`#region Constructor Tests`, etc.) |
| Formatting | 4-space indentation, braces on own line |
| String interpolation | Preferred over `String.Format`; `Settings.GetConnectionString()` uses `SqlConnectionStringBuilder` |

---

## 14. External API Reference

### 14.1 Weather.com (Weather Underground) API

- **Endpoint**: `https://api.weather.com/v3/wx/forecast/daily/5day`
- **Auth**: API key in query string (`apiKey=`
- **Parameters**: `geocode={lat},{lon}`, `format=json`, `units=e`, `language=en-US`
- **Returns**: JSON with arrays: `temperatureMax[]`, `temperatureMin[]`, `validTimeLocal[]`, `daypart[0].windDirection[]`, `daypart[0].windSpeed[]`, `daypart[0].iconCode[]`, `daypart[0].narrative[]`, etc.
- **Rate limit**: Subject to API plan. HTTP 401 on invalid/expired key.

### 14.2 Open-Meteo API

- **Endpoint**: `https://api.open-meteo.com/v1/forecast`
- **Auth**: None (free tier, no API key required)
- **Parameters**: `latitude`, `longitude`, hourly variables (`temperature_2m`, `relative_humidity_2m`, `precipitation_probability`, `pressure_msl`, `wind_speed_10m`, `wind_direction_10m`, `weather_code`, `rain`), daily variables (`temperature_2m_max`, `temperature_2m_min`), `timezone=auto`
- **Returns**: JSON with `hourly` and `daily` objects containing arrays of values.

---

## 15. Key Design Decisions

1. **Two workers, country-based station split**: Stations are split by `country`. Worker Wg handles Canadian stations (`country = 'CA'`, up to 1000), Worker Open handles US stations (`country = 'US'`, up to 1400). Order is randomized via `CHECKSUM(NEWID(), sid)`. This ensures each station gets weather data from exactly one source per day.

2. **Sequential execution, not parallel**: Workers run one after the other to avoid overwhelming the database connection and to simplify error handling.

3. **Dynamic delay calculation**: Instead of a fixed delay, the delay between API calls is calculated to fill the 8-hour budget evenly. This prevents finishing too early (wasting API capacity) or too late (missing the daily window).

4. **401 stops current worker only**: An invalid API key on Worker Wg doesn't prevent Worker Open from running (since Open-Meteo doesn't require a key). The service continues to the next worker and waits for the next day.

5. **Trigger-based JSON parsing**: The service writes raw JSON to `ows_meteo`. A SQL trigger (`TR_ows_meteo`) fires `sp_ows_meteo` to parse it server-side. This keeps parsing logic in SQL and the C# service simple.

6. **Registry-based configuration**: Settings are stored in `HKLM\SOFTWARE\FishFind\OWMService` rather than `App.config` to allow changes without redeploying the binary.

7. **Sleep until midnight**: After both workers finish, the service sleeps until 00:00 next day rather than using a fixed interval. This ensures exactly one full cycle per day aligned to calendar days.
