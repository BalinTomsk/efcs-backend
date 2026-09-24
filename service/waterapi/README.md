# waterapi

Read-only C# / .NET 10 HTTP API that serves cached water-station data to the web frontend.
Release history: [`CHANGELOG.md`](CHANGELOG.md). The endpoints are summarised below.

## Endpoints

| Method | Path | Port | Purpose |
|--------|------|------|---------|
| GET | `/api/v1/water/station/map?country=US[&state=MN]` | 8080 | every map pin for a country (ETag / 304) |
| GET | `/api/v1/water/station/{sid}` | 8080 | one map station |
| GET | `/health` | 8080 | `{ status, version, uptime }` |
| GET | `/health/live`, `/health/ready`, `/metrics` | 8081 | private management — do not publish |

## Build, test, run

```bash
dotnet build WaterApi.slnx
dotnet run --project WaterApi.Tests
dotnet run --project WaterApi --no-launch-profile
```

With no `DB_URL` the service runs on an in-memory backing that returns no stations. To read the real
database, copy `.env.example` to `.env` (or point `DOTENV_PATH` at an env file) and set
`DB_URL` / `DB_USERNAME` / `DB_PASSWORD`.

## Docker

```bash
docker build -t waterapi:dev .
docker run --rm -p 8090:8080 -e DOTENV_PATH=/run/secrets/waterapi.env -v "$PWD/.env:/run/secrets/waterapi.env:ro" waterapi:dev
```
