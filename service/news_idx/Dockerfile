# syntax=docker/dockerfile:1

# ---- Build stage -------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NewsIdx/NewsIdx.csproj NewsIdx/
COPY NewsIdx.Tests/NewsIdx.Tests.csproj NewsIdx.Tests/
RUN dotnet restore NewsIdx/NewsIdx.csproj -r linux-x64

# Self-contained linux-x64, like the siblings: the Debian 13 runtime image needs no .NET runtime.
COPY NewsIdx/ NewsIdx/
RUN dotnet publish NewsIdx/NewsIdx.csproj \
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

# BGE-small-en-v1.5 (model.onnx + vocab.txt, ~130 MB). Not in git: run scripts/fetch-model.ps1 first, which
# downloads the pinned revision and checks its SHA-256. The build fails here if the folder is missing.
COPY models/bge-small-en-v1.5/model.onnx models/bge-small-en-v1.5/vocab.txt ./models/bge-small-en-v1.5/

RUN groupadd --system --gid 10001 app \
 && useradd --system --uid 10001 --gid app --no-create-home app \
 && mkdir -p /app/logs && chown -R app:app /app/logs
USER 10001:10001

ENV TZ=UTC \
    DOTNET_TieredPGO=1

# 8080 = public search API + /health. 8081 = private metrics/probes/admin (do NOT publish it).
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["/app/NewsIdx"]
