# syntax=docker/dockerfile:1

# ---- Build stage -------------------------------------------------------------------------------
# .NET 10 SDK (the GA "10.0" base is Ubuntu; it only builds here — the runtime image is Debian 13).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached unless project files change). Restore for the linux-x64 runtime we publish for.
COPY WaterService/WaterService.csproj WaterService/
COPY WaterService.Tests/WaterService.Tests.csproj WaterService.Tests/
RUN dotnet restore WaterService/WaterService.csproj -r linux-x64

# Publish SELF-CONTAINED for linux-x64 so the Debian 13 runtime image needs no .NET runtime installed.
# (Microsoft does not yet ship a GA .NET 10 Debian-trixie image, so we bundle the runtime instead.)
COPY . .
RUN dotnet publish WaterService/WaterService.csproj \
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

# Run as an unprivileged user. Pre-create the writable logs directory owned by that user so the
# container can run with a read-only root filesystem (mount a volume/tmpfs at /app/logs).
RUN groupadd --system --gid 10001 app \
 && useradd --system --uid 10001 --gid app --no-create-home app \
 && mkdir -p /app/logs && chown -R app:app /app/logs
USER 10001:10001

# Keep the container in UTC — the persistence layer stores UTC wall-clock timestamps.
ENV TZ=UTC \
    DOTNET_TieredPGO=1

# 8080 = public /health probe surface. 8081 = private metrics/liveness/readiness (do NOT publish it).
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["/app/WaterService"]
