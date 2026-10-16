# syntax=docker/dockerfile:1

# ---- Build stage -------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NewsSearch/NewsSearch.csproj NewsSearch/
COPY NewsSearch.Tests/NewsSearch.Tests.csproj NewsSearch.Tests/
RUN dotnet restore NewsSearch/NewsSearch.csproj -r linux-x64

# Self-contained linux-x64, like the siblings: the Debian 13 runtime image needs no .NET runtime.
COPY NewsSearch/ NewsSearch/
RUN dotnet publish NewsSearch/NewsSearch.csproj \
        -c Release -r linux-x64 --self-contained true \
        -p:PublishTrimmed=false \
        -o /app/publish

# ---- Runtime stage: Debian 13 ("trixie") -------------------------------------------------------
FROM debian:trixie-slim AS runtime
WORKDIR /app

# curl for the HEALTHCHECK; libicu for globalization (do not switch to invariant mode).
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl libicu76 \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

RUN groupadd --system --gid 10001 app \
 && useradd --system --uid 10001 --gid app --no-create-home app \
 && mkdir -p /app/logs && chown -R app:app /app/logs
USER 10001:10001

ENV TZ=UTC \
    DOTNET_TieredPGO=1

# 8080 = public search API + /health. 8081 = private metrics/probes (do NOT publish it).
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["/app/NewsSearch"]
