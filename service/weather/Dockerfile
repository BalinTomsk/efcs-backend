# syntax=docker/dockerfile:1

# ---- Build stage -------------------------------------------------------------------------------
# .NET 10 SDK (the GA "10.0" base is Ubuntu; it only builds here — the runtime image is Debian 13).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached unless project files change). Restore for the linux-x64 runtime we publish for.
COPY WeatherService/WeatherService.csproj WeatherService/
COPY WeatherService.Tests/WeatherService.Tests.csproj WeatherService.Tests/
RUN dotnet restore WeatherService/WeatherService.csproj -r linux-x64

# Publish SELF-CONTAINED for linux-x64 so the Debian 13 runtime image needs no .NET runtime installed.
# (Microsoft does not yet ship a GA .NET 10 Debian-trixie image, so we bundle the runtime instead.)
COPY . .
RUN dotnet publish WeatherService/WeatherService.csproj \
        -c Release -r linux-x64 --self-contained true \
        -p:PublishTrimmed=false \
        -o /app/publish

# ---- Runtime stage: Debian 13 ("trixie") -------------------------------------------------------
# Pin by digest (debian:trixie-slim@sha256:...) for reproducible, tamper-resistant builds.
FROM debian:trixie-slim AS runtime
WORKDIR /app

# Native deps for a self-contained .NET app + curl for the HEALTHCHECK. Microsoft.Data.SqlClient
# requires ICU (libicu76 on Debian 13) — it does not support globalization-invariant mode; openssl
# provides libssl for SqlClient TLS.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl openssl libicu76 \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

# Run as an unprivileged user. Pre-create the writable logs directory owned by that user: it holds the
# rolling logs AND the .lifecycle state (crash marker, incidents, API-usage ledger), so mount a volume
# here to keep crash history and daily quota accounting across a redeploy.
RUN groupadd --system --gid 10001 app \
 && useradd --system --uid 10001 --gid app --no-create-home app \
 && mkdir -p /app/logs && chown -R app:app /app/logs
USER 10001:10001

# Keep the container in UTC — the persistence layer stores UTC wall-clock timestamps.
ENV TZ=UTC \
    DOTNET_TieredPGO=1

# 8081 carries the health endpoints; the app has no other HTTP surface. Nothing listens on 8080.
EXPOSE 8081

# Liveness only: a DB outage must not mark the container unhealthy — the workers sleep and recover on
# their own. /actuator/health/readiness is the DB-dependent probe.
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD curl -fsS http://localhost:8081/actuator/health/liveness || exit 1

ENTRYPOINT ["/app/WeatherService"]
