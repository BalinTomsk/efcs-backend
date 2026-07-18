# OWMService — Weather Data Service for FishFind

[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.7.2-blue)](https://dotnet.microsoft.com/download/dotnet-framework)
[![C#](https://img.shields.io/badge/C%23-8.0-239120)](https://docs.microsoft.com/en-us/dotnet/csharp/)
[![SQL Server](https://img.shields.io/badge/SQL%20Server-2019+-CC2927)](https://www.microsoft.com/en-us/sql-server)
[![License](https://img.shields.io/badge/license-MIT-green)](#license)

**OWMService** is a production Windows Service that fetches daily weather forecasts from two external APIs for ~2,400 water-monitoring stations, stores the raw JSON in SQL Server, and feeds a real-time fish-catch probability engine used by [fishfind.info](http://fishfind.info).

---

## How It Works

I. OWMService is a Windows Service that periodically retrieves weather data and updates the FishFind database.

1.  OWMService  service reads a list of water state stations from:  
    `select top 100 mli, lat, lon, state from [WaterStation] w where exists (select * from lake_fish f where f.lake_Id = w.lakeId)`
2.  Weather data is retrieved as JSON for the closest weather station using the API:  
    `"https://api.weather.com/v3/wx/forecast/daily/5day?geocode={lat},{lon}&format=json&units=e&language=en-US&apiKey={settings.Wunderground}"
3.  The JSON data is saved into the database with:  
    `UPDATE [ows_meteo] SET ows = @js WHERE mli = @mli`
4.  A trigger `[ows_meteo]` on `TR_ows_meteo` runs:  
    `EXEC sp_ows_meteo @json, @mli, @WaterStation_id`
5.  `sp_ows_meteo` parses the passed JSON and updates/merges data into `[weather_Forecast]`
6.  `spTotalUpdateProbability` is executed to update fish probability

II. Water Data State
 1. 
 2. 

---

## Requirements

- Windows 10 / 11 / Server 2016+
- .NET Framework **4.7.2**
- SQL Server 2016+ (uses `STRING_SPLIT`, `JSON_QUERY`, `OPENJSON`)
- Administrator privileges (service installation + registry access)
- Weather.com API key ([get one here](https://www.wunderground.com/member/api-keys))

---

## Build

Open `OWMService.sln` in **Visual Studio 2022** and build:

```
Build → Build Solution
```

The executable will be generated in:

```
bin\Release\OWMService.exe
```

or

```
bin\Debug\OWMService.exe
```

---

# Configuration

### Registry

The service reads settings from:

```
HKEY_LOCAL_MACHINE\SOFTWARE\FishFind\OWMService
```

Expected values:

| Name | Type | Description |
|---|---|---|
| `Server` | REG_SZ | SQL Server hostname |
| `dbName` | REG_SZ | Database name (default: `fishfind`) |
| `userName` | REG_SZ | SQL login |
| `userPassword` | REG_SZ | SQL password |
| `wunderground` | REG_SZ | Weather.com API key |
| `Interval` | REG_DWORD | Reserved for future use |

Import the template: `Res\OWMService.reg`, then update the values.

### App.config

| Key | Default | Description |
|---|---|---|
| `LogFilePath` | *(empty)* | Custom log path. Default: `C:\ProgramData\OWMService\Logs\OWMService.log` |
| `EventLogSource` | `OWMService` | Windows Event Log source |
| `EventLogName` | `Application` | Windows Event Log name |

### Database Setup

Run `Res\OWMService.sql` on your SQL Server instance to create:
- `ows_meteo` — raw JSON storage table
- `weather_Forecast` — parsed forecast data
- `sp_ows_meteo` — JSON parsing stored procedure
- `TR_ows_meteo` — UPDATE trigger

---

## Install & Run

### Install the Service

Open **Command Prompt as Administrator**.

Run:

```
sc create OWMService binPath= "C:\Path\To\OWMService.exe"
```

Example:

```
sc create OWMService binPath= "C:\Services\OWMService\OWMService.exe"
```

### Start the Service

```
sc start OWMService
```

or via **Services Manager**:

```
services.msc
```

Find **OWMService** and click **Start**.

---

# Stop the Service

```
sc stop OWMService
```

---

# Deregister / Remove the Service

Stop the service first:

```
sc stop OWMService
```

Then delete it:

```
sc delete OWMService
```

---

# Debugging (Console Mode)

When running inside Visual Studio, the service can run in **console mode** for debugging.

Start with **F5**.

The application will run as a console:

```
Press Enter to stop...
```

Supports breakpoints, console logging, and single-pass execution of both workers.

---

## Logging

### File Log

Default location: `C:\ProgramData\OWMService\Logs\OWMService.log`

### Windows Event Log

---

## Error Handling

| Scenario | Behavior |
|---|---|
| Empty/null connection string | Returns `false` immediately |
| SQL connection failure | Logged, returns `false` |
| Station query failure | Logged with SQL query text, returns `false` |
| **HTTP 401 Unauthorized** | Stops current worker, skips `ProcessFishState`, next worker still runs |
| HTTP error (non-401) | Station skipped, processing continues |
| Network timeout | Station skipped, processing continues |
| Trigger failure on save | Logged, JSON payload saved to `Logs\failed_{mli}_{timestamp}.json` |
| Stored procedure failure | Logged, does not fail the worker |
| Unhandled exception in timer | Logged, sleeps until next day |

---

## Testing

| Test | Validates |
|---|---|
| `Constructor_WithValidLogger` | Worker instantiates correctly |
| `Constructor_WithNullLogger` | Guard clause throws `ArgumentNullException` |
| `Process_WithEmptyConnectionString` | Early exit returns `false` |
| `Process_WithInvalidConnection` | Connection failure logged |
| `Process_WithVariousInvalidSettings` | Theory: null, empty, whitespace |
| `WeatherDataWorker_ShouldImplementIWeatherDataWorker` | Interface contract |

---

## External APIs

### Weather.com (Weather Underground)

Returns 5-day forecast with temperature, wind, precipitation, and icon arrays.

### Open-Meteo

Free tier, no API key required. Returns hourly and daily forecast arrays.

---

## Related Services

This repository is part of the **FishFind** backend ecosystem:

| Service | Technology | Description |
|---|---|---|
| **OWMService** *(this)* | C# / .NET Framework | Weather data collection + fish probability engine |
| **water-station-pusher** | Java / Spring Boot 3 | Reads hydrometric data from Environment Canada (CA) and USGS (US) |
| **auth-service** | Java / Spring Boot 3 | Authentication and user management |

---

## Documentation

- [`docs/specification.md`](docs/specification.md) — Full technical specification (enough to recreate the entire application)

---

## Troubleshooting

<details>
<summary><strong>Service fails to start</strong></summary>

Check the Windows Event Log:
```
Winevtlist
```

</details>

<details>
<summary><strong>Database connection errors</strong></summary>

Verify registry values:
```
reg query "HKEY_LOCAL_MACHINE\SOFTWARE\FishFind\OWMService"
```

Ensure the SQL Server is reachable and the credentials are valid.
</details>

<details>
<summary><strong>401 Unauthorized errors repeating</strong></summary>

The Weather.com API key is invalid or expired. Get a new key at [wunderground.com/member/api-keys](https://www.wunderground.com/member/api-keys) and update the `wunderground` registry value. The service will retry on the next daily cycle.
</details>

<details>
<summary><strong>Service not installing</strong></summary>

Ensure the command prompt is running **as Administrator**. The `sc create` command requires elevated privileges.
</details>

---

## License

This project is part of the FishFind platform — [fishfind.info](http://fishfind.info)