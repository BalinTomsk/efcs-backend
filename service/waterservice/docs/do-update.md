# Deploy / update `water-station-pusher-cs` on DigitalOcean

Runbook for building the C# (.NET 10) water service image and deploying it to the DigitalOcean droplet.
Reflects the live deployment as of **2026-07-18** (release `10.0.1` — full-scale, all stations, with
`RunOnStartup`).

> **Never inline real secrets in this file.** Use placeholders. The GHCR PAT and the DB credentials are
> read from a secure source at run time (see below). A committed credential is a leak.

## Configuration

| Key | Value |
|-----|-------|
| Service | `water-station-pusher` (C# / .NET 10 port) |
| Local project | `C:\envoinx\fishfind\efcs-backend\service\waterservice` |
| Local Docker engine | Rancher Desktop (dockerd inside the `rancher-desktop` WSL VM) |
| Registry | GitHub Container Registry (GHCR) |
| GHCR image | `ghcr.io/balintomsk/water-station-pusher-cs:<TAG>` |
| GHCR user | `BalinTomsk` |
| Droplet | `root@137.184.218.128` (`debian-csnode`, Debian 13, amd64) |
| Container name | `water-station-pusher-cs` |
| Attached volume | `volume-env` → `/mnt/volume_env` (ext4; persisted via a `nofail` fstab entry) |
| Env file on droplet | `/mnt/volume_env/waterservice/waterservice.env` (owner `10001:10001`, mode `0400`) |
| Container env mount | `/run/secrets/waterservice.env` (read via `DOTENV_PATH`) |
| Logs on droplet | `/mnt/volume_env/waterservice/logs` → `/app/logs` (Serilog JSON, daily roll, 7-day retention) |
| Published port | `8080` (health). `8081` (metrics/liveness/readiness) is **not** published |

## Related Java services (coexistence — read this)

This C# service is a **port of the Java `waterservice`** (Spring Boot) in the sibling repo
`efj-backend/service`. That Java backend has three services — **`waterservice`**, **`weather`**, and
**`auth`** — plus a combined image (`efj-backend/service/Dockerfile` + `start-services.sh`) that can run
water + weather together in one container (`fishfind-station-services`).

**By design — dual-service redundancy (do NOT retire either).** The Java **`water-station-pusher`** runs on
a different droplet (`debian-jnode` / `68.183.196.166`) and writes to the **same production DB** as this C#
service, polling the same feeds *independently*. This is an **intentional "double warranty"** on incoming
data — **keep both running.** Writes go through `dbo.sp_UpdateWaterData` keyed by `(mli, stamp)` as an
upsert, so the two services' concurrent writes collapse to the same rows; if one pipeline is down, slow, or
a feed fetch fails on one side, the other still lands the data. The post-processing procs
(`sp_clean_old_water_data`, `spPushSpeciesFromLakeToStation`) therefore run once per service per cycle —
expected and tolerated. The Java deployment runbook is
`efj-backend/service/waterservice/docs/do-update.md` (GHCR image
`ghcr.io/balintomsk/water-station-pusher`, env at `/mnt/volume_jnode/waterservice/waterservice.env` on
`debian-jnode`). The Java `weather` and `auth` services are unrelated to this container and are not touched
by this runbook.

## Prerequisites

- **Rancher Desktop running.** The Windows `docker` CLI cannot reach the daemon on this machine; build
  through the VM (`rdctl shell`). If `buildkitd`/dockerd is not up, start Rancher Desktop first.
- **GHCR PAT** with `write:packages` + `read:packages` (starts with `ghp_`). Keep it out of this file —
  export it as `$GHCR_PAT` (local) / pass it on the droplet from a secure source.
- **SSH key access** to `root@137.184.218.128` (already configured).
- **Attached volume `volume-env`** mounted at `/mnt/volume_env` with
  `/mnt/volume_env/waterservice/waterservice.env` present (see Step 5 to (re)create it + persist the mount).

---

## Step 0 — Set the version tag

Local (PowerShell):

```powershell
Set-Item Env:TAG "10.0.1"
```

Everything below runs the build/push inside the Rancher VM, where dockerd lives. The Windows project
path is mounted at `/mnt/c/...`.

## Step 1 — Build the image (inside the Rancher VM)

```powershell
rdctl shell sh -c "cd /mnt/c/envoinx/fishfind/efcs-backend/service/waterservice && docker build -t water-station-pusher-cs:$env:TAG ."
```

The Dockerfile publishes a **self-contained linux-x64** app onto **Debian 13 (`debian:trixie-slim`)** —
GA .NET 10 has no Debian-trixie base image, so the runtime is bundled. Runs as non-root uid 10001.

## Step 2 — Log in to GHCR (inside the VM)

```powershell
rdctl shell sh -c "echo $env:GHCR_PAT | docker login ghcr.io -u BalinTomsk --password-stdin"
```

## Step 3 — Tag for GHCR

```powershell
rdctl shell sh -c "docker tag water-station-pusher-cs:$env:TAG ghcr.io/balintomsk/water-station-pusher-cs:$env:TAG && docker tag water-station-pusher-cs:$env:TAG ghcr.io/balintomsk/water-station-pusher-cs:latest"
```

## Step 4 — Push

```powershell
rdctl shell sh -c "docker push ghcr.io/balintomsk/water-station-pusher-cs:$env:TAG && docker push ghcr.io/balintomsk/water-station-pusher-cs:latest"
```

---

## Step 5 — (First time only) attached volume + env file

The secret lives on the DigitalOcean **attached volume `volume-env`** (mounted at `/mnt/volume_env`), not
on the root disk. Skip if `/mnt/volume_env/waterservice/waterservice.env` already exists.

**5a — persist the volume mount across reboots.** DO attaches the volume but does not add an fstab entry,
so after a reboot it would be unmounted and the container's secret mount would be empty. Add a `nofail`
entry (idempotent; `nofail` means a missing volume never blocks boot):

```bash
ssh root@137.184.218.128 '
  grep -q scsi-0DO_Volume_volume-env /etc/fstab || \
    echo "/dev/disk/by-id/scsi-0DO_Volume_volume-env /mnt/volume_env ext4 defaults,nofail,discard 0 0" >> /etc/fstab
  mount -a && mountpoint /mnt/volume_env'
```

**5b — place the env file on the volume.** It holds the DB credentials (shared JDBC-style `DB_URL` +
`DB_USERNAME` + `DB_PASSWORD`); it must be owned by uid 10001 and readable only by the owner so the
non-root container can read it. Stage it locally from your secret store (`efcs-backend/secret/.env`) and
copy it up — do **not** type the password into a shell command:

```bash
# from a machine that has the secret .env
ssh root@137.184.218.128 'mkdir -p /mnt/volume_env/waterservice'
scp ./waterservice.env root@137.184.218.128:/mnt/volume_env/waterservice/waterservice.env
ssh root@137.184.218.128 'chown 10001:10001 /mnt/volume_env/waterservice/waterservice.env && chmod 0400 /mnt/volume_env/waterservice/waterservice.env && ls -l /mnt/volume_env/waterservice/waterservice.env'
```

**5c — create the logs directory on the volume.** The container writes its Serilog rolling file to
`/app/logs`; back it with the volume so logs survive redeploys/reboots. It must be writable by uid 10001:

```bash
ssh root@137.184.218.128 'mkdir -p /mnt/volume_env/waterservice/logs && chown 10001:10001 /mnt/volume_env/waterservice/logs && chmod 0755 /mnt/volume_env/waterservice/logs'
```

Contents (values from the secret store — placeholders shown here):

```dotenv
DB_URL=jdbc:sqlserver://s31.winhost.com:1433;databaseName=DB_111487_fish;encrypt=true;trustServerCertificate=true
DB_USERNAME=<db_user>
DB_PASSWORD=<db_password>
```

> If the file is ever owned by the wrong user, the app now **falls back to real environment variables
> instead of crashing** — but it will run without DB config unless env vars are supplied, so fix the
> ownership.

## Step 6 — Log in to GHCR on the droplet (private package pull)

```bash
ssh root@137.184.218.128
echo "<GHCR_PAT>" | docker login ghcr.io -u BalinTomsk --password-stdin
export TAG="10.0.1"
```

## Step 7 — Pull the new image

```bash
docker pull ghcr.io/balintomsk/water-station-pusher-cs:$TAG
```

## Step 8 — Replace the container

```bash
docker rm -f water-station-pusher-cs 2>/dev/null || true
docker run -d \
  --name water-station-pusher-cs \
  --restart unless-stopped \
  --log-driver json-file --log-opt max-size=10m --log-opt max-file=3 \
  -v /mnt/volume_env/waterservice/waterservice.env:/run/secrets/waterservice.env:ro \
  -v /mnt/volume_env/waterservice/logs:/app/logs \
  -e DOTENV_PATH=/run/secrets/waterservice.env \
  -e Water__Worker__RunOnStartup=true \
  -p 8080:8080 \
  ghcr.io/balintomsk/water-station-pusher-cs:$TAG
```

Notes:
- Only `8080` is published. Port `8081` (metrics / liveness / readiness) stays on the container network —
  reach it with `docker exec` (Step 9), never publish it.
- **`Water__Worker__RunOnStartup=true`** makes the container run one **full cycle (all ~11,700 stations)
  immediately** after (re)deploy, then continue on the hourly cron. A full cycle takes ~2–3h and overruns
  the hour by design (same as the Java clone). Omit this env var for pure cron scheduling (default is off);
  note that with `--restart unless-stopped` it also re-runs a full cycle after any restart/reboot.
- Otherwise the in-process scheduler fires at the top of every hour (`0 0 * * * *`). For a one-off manual
  cycle, run the image with `--console` (see below).

## Step 9 — Verify

```bash
docker ps --filter name=water-station-pusher-cs --format "table {{.Names}}\t{{.Image}}\t{{.Status}}"
# Docker HEALTHCHECK (curl /health): expect "healthy" after ~30s
docker inspect --format '{{.State.Health.Status}}' water-station-pusher-cs
# External health probe
curl -fsS http://localhost:8080/health; echo
# Readiness — real DB SELECT 1 (8081 is unpublished, so exec into the container)
docker exec water-station-pusher-cs curl -fsS http://localhost:8081/health/ready; echo   # -> Healthy
# Startup logs should show the schedule + listeners and no errors
docker logs --tail 30 water-station-pusher-cs
```

## Rollback

```bash
export PREV_TAG="1.0.0"
docker rm -f water-station-pusher-cs
docker run -d --name water-station-pusher-cs --restart unless-stopped \
  --log-driver json-file --log-opt max-size=10m --log-opt max-file=3 \
  -v /mnt/volume_env/waterservice/waterservice.env:/run/secrets/waterservice.env:ro \
  -e DOTENV_PATH=/run/secrets/waterservice.env -p 8080:8080 \
  ghcr.io/balintomsk/water-station-pusher-cs:$PREV_TAG
```

## Run one cycle manually (console mode)

Processes a single cycle then exits — optionally one station. **Writes to prod** (upserts + cleanup +
species-push), same as a scheduled cycle.

```bash
docker run --rm \
  -v /mnt/volume_env/waterservice/waterservice.env:/run/secrets/waterservice.env:ro \
  -v /mnt/volume_env/waterservice/logs:/app/logs \
  -e DOTENV_PATH=/run/secrets/waterservice.env \
  ghcr.io/balintomsk/water-station-pusher-cs:$TAG --console --station=02HA014
```

## Operational notes

- **Dual-service redundancy (Java + C#)** — see [Related Java services](#related-java-services-coexistence--read-this)
  at the top. Both services intentionally run in parallel against the same prod DB as a "double warranty" on
  incoming data (idempotent upserts by `(mli, stamp)`); **keep both running — do not retire either.**
- **Timezone.** The container runs in UTC (persistence stores UTC wall-clock). Keep it that way.
- **GHCR PAT rotation.** The PAT historically embedded in the Java runbook is considered leaked — rotate
  it and never paste a real PAT into this file.
- **Logs.** JSON (Serilog): to stdout (Docker json-file, capped/rotated via the run flags above) **and**
  to a daily rolling file with 7-day retention at `/app/logs`, which is bind-mounted to the volume at
  `/mnt/volume_env/waterservice/logs` so it survives container redeploys and reboots. Inspect on the
  droplet with `tail -f /mnt/volume_env/waterservice/logs/*.log`. (Docker's own stdout json logs stay on
  the root disk under `/var/lib/docker`.)

## Quick command summary

Local (build + push, inside the Rancher VM):

```powershell
Set-Item Env:TAG "10.0.1"
rdctl shell sh -c "cd /mnt/c/envoinx/fishfind/efcs-backend/service/waterservice && docker build -t water-station-pusher-cs:$env:TAG ."
rdctl shell sh -c "echo $env:GHCR_PAT | docker login ghcr.io -u BalinTomsk --password-stdin"
rdctl shell sh -c "docker tag water-station-pusher-cs:$env:TAG ghcr.io/balintomsk/water-station-pusher-cs:$env:TAG && docker push ghcr.io/balintomsk/water-station-pusher-cs:$env:TAG"
```

Droplet (pull + run):

```bash
ssh root@137.184.218.128
echo "<GHCR_PAT>" | docker login ghcr.io -u BalinTomsk --password-stdin
export TAG="10.0.1"
docker pull ghcr.io/balintomsk/water-station-pusher-cs:$TAG
docker rm -f water-station-pusher-cs
docker run -d --name water-station-pusher-cs --restart unless-stopped \
  --log-driver json-file --log-opt max-size=10m --log-opt max-file=3 \
  -v /mnt/volume_env/waterservice/waterservice.env:/run/secrets/waterservice.env:ro \
  -e DOTENV_PATH=/run/secrets/waterservice.env -p 8080:8080 \
  ghcr.io/balintomsk/water-station-pusher-cs:$TAG
docker logs -f water-station-pusher-cs
```
