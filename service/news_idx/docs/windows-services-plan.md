# Plan: Qdrant + news_idx as Windows services on CTRL (no Docker, no sign-in)

Status (2026-10-20): **code done (news_idx 0.5.0, branch `news-idx-windows-service`, not committed)**, published to
`out/win-x64` (git-ignored) and smoke-tested there; nothing on CTRL has changed yet.

## Why

On 2026-10-20, CTRL rebooted twice, and both times search was down until someone signed in. Qdrant and news_idx run
in Rancher Desktop, a per-user desktop app, so they start only after a user signs in. Autologon plus the 5-minute
`news_idx watchdog` task is a workaround: it depends on an unlocked auto sign-in, a stored password and a GUI app.
Windows services start at boot with nobody signed in, and Service Control Manager restarts them when they crash.

## Target

| piece | runs as | listens on | data / files |
|---|---|---|---|
| **Qdrant 1.19.0** (`qdrant.exe`, official `x86_64-pc-windows-msvc` build) | Windows service `qdrant` via WinSW, LocalService | `127.0.0.1:6333` (REST), `127.0.0.1:6334` (gRPC); never the LAN | `C:\qdrant\storage`, `C:\qdrant\snap` |
| **news_idx** (self-contained `win-x64` publish) | Windows service `news_idx`, native (`UseWindowsService`), virtual account `NT SERVICE\news_idx` | `0.0.0.0:8080` (public, firewalled as today), **`127.0.0.1:8081`** (management) | `C:\news_idx\app\` (binaries + `models\`), `C:\news_idx\app\logs\`, env `C:\news_idx\news_idx.env` |

Both: startup type **Automatic (Delayed Start)**, and recovery set to restart after 10 s, 30 s and 60 s
(`sc failure`). news_idx depends on qdrant (`sc config news_idx depend= qdrant`). news_idx already retries with
back-off when Qdrant is not up yet, so the dependency only saves log noise.

**Keep Qdrant's storage path short.** Measured locally 2026-10-16: under a deep path, Qdrant 1.19 on Windows fails
to create a collection ("Failed to create storage for mmap sparse vectors ... os error 3").

## Code changes (news_idx 0.5.0) — DONE

1. **Windows service host.** Add `Microsoft.Extensions.Hosting.WindowsServices` and call
   `builder.Host.UseWindowsService()`. This does nothing outside a service, so Docker and `dotnet run` are unchanged.
2. **Paths anchored to the app folder.** A service starts in `C:\Windows\System32`. The Serilog file sink
   (`logs/news_idx.log`), the model paths (`models/bge-small-en-v1.5/...`) and a relative `DOTENV_PATH` are all
   relative. As built: when `WindowsServiceHelpers.IsWindowsService()`, the process first runs
   `Directory.SetCurrentDirectory(AppContext.BaseDirectory)`. One line covers all of them, and `dotnet run` and the
   tests keep resolving `models/` from the service folder. Resolving each path against the base directory instead
   would have broken both.
3. **Management bind address.** `UseUrls("http://0.0.0.0:8080", "http://0.0.0.0:8081")` is fine in Docker, where
   8081 is never published. On bare Windows, 8081 would sit on the LAN, protected only by the firewall default.
   Make the management address configurable, e.g. `NewsIdx__ManagementUrl` (default `http://0.0.0.0:8081`, CTRL sets
   `http://127.0.0.1:8081`). Keep `Connection.LocalPort == 8081` as the guard: it still holds.
4. Tests: `ManagementUrl` default / narrowing (4 cases) and refusals (4). Suite 80 pass + the real-Qdrant test
   skipped. The working-directory switch only happens under SCM, so it is checked at cutover (the log file must
   appear in `C:
ews_idxpp\logs\`). Smoke test of the published exe on the workstation against Qdrant 1.19
   (Windows binary): `/health` 0.5.0; 8080 on `0.0.0.0` and 8081 on `127.0.0.1` only (`netstat`); the model loaded
   from `models/`; vector and bm25 search 200; `/admin` 404 on 8080 and the probes 200 on 8081. Publish:
   `dotnet publish NewsIdx -c Release -r win-x64 --self-contained -o out\win-x64`, then copy `models\` next to it.

## Cutover on CTRL

Search stays up until step 5. The old containers stay in place as the rollback.

1. **Qdrant service.** Unzip `qdrant.exe` to `C:\qdrant\`. Add WinSW (`qdrant-service.exe` + `qdrant-service.xml`:
   env `QDRANT__STORAGE__STORAGE_PATH=C:\qdrant\storage`, `QDRANT__STORAGE__SNAPSHOTS_PATH=C:\qdrant\snap`,
   `QDRANT__SERVICE__HOST=127.0.0.1`, `QDRANT__TELEMETRY_DISABLED=true`, log rotation). Install, but do not
   start it yet: the container Qdrant is not published on the host, so 6333/6334 are free, but keep one writer at a
   time.
2. **Copy the data with snapshots, not a re-index.** A re-index takes ~50 min and re-embeds everything. Instead,
   in the container: `POST /collections/news_articles/snapshots` and `.../news_articles_bm25/snapshots`, then
   `docker cp` both `.snapshot` files out. Start the `qdrant` service and recover each collection with
   `PUT /collections/{name}/snapshots/recover` (a `file:///` location) or the upload endpoint. Compare
   `points_count` with the container's, which today is 19,168 vector points and 4,900 BM25 points.
3. **news_idx service.** Copy the publish folder to `C:\news_idx\app\`. Run `sc create news_idx binPath=
   "C:\news_idx\app\NewsIdx.exe" start= delayed-auto obj= "NT SERVICE\news_idx"`, then `sc failure` and
   `sc config ... depend= qdrant`. Set the environment in the service's registry `Environment` value or in
   `news_idx.env`: `DOTENV_PATH=C:\news_idx\news_idx.env`, `QDRANT_URL=http://127.0.0.1:6334`,
   `NewsIdx__Embedding__Threads=6`, `NewsIdx__Search__MinScore=0.65`, `NewsIdx__ManagementUrl=http://127.0.0.1:8081`,
   and a GC heap cap in place of `--memory 2g` (`DOTNET_GCHeapHardLimit=0x60000000`, 1.5 GB). Grant
   `NT SERVICE\news_idx` read on `news_idx.env` (keep its Administrators + SYSTEM ACL) and modify on `app\logs\`.
4. **Firewall.** The existing block rule `news_idx 8080 - block all but cproxy, csnode and LAN` is port-based, so
   it keeps applying. The allow rule today belongs to Rancher's `host-switch.exe`, so add `news_idx 8080 allow`
   (TCP 8080 inbound, program `C:\news_idx\app\NewsIdx.exe`). A block rule beats an allow, so the exclusions hold.
5. **Switch** (about a minute of downtime): stop the `news_idx` container to free host port 8080, then
   `sc start news_idx`. Check `/health` (version), `/api/v1/newsidx/bm25?q=keta` and `/search`, the LAN probe,
   and through cproxy `/api/v1/newssearch/{bm25,vector}`. Check the admin status page too: news_idx reports
   Qdrant itself.
6. **Retire the Docker path**, after a reboot test with nobody signed in. Disable the tasks `news_idx watchdog`
   and `lock console after auto sign-in`, stop the `qdrant` container, and turn Autologon off (better security:
   no stored sign-in). Keep the images and the `qdrant-data` volume for a week, then remove them.

## Rollback

`sc stop news_idx` and `sc stop qdrant`, then start the `qdrant` and `news_idx` containers. The container data is
untouched, but it misses edits indexed while the services ran: run `POST /admin/sync` afterwards.

## Deploys afterwards

These replace the `d.cmd build` / `run` steps in `do-update.md` and `secret/deploy-bm25.ps1`:
1. Publish `win-x64` locally.
2. Copy a zip to `C:\news_idx\release\<ver>\`.
3. `sc stop news_idx`, swap `app\` (keep the previous folder as `app.prev\`), `sc start news_idx`.

There is no image build on CTRL, and the stale-file problem from 2026-10-16 (`NewsFindOptions.cs`) cannot happen.

## Open points for the owner

- WinSW is a third-party download (MIT, github.com/winsw/winsw). The alternative is a Task Scheduler task "At
  startup, run whether user is logged on or not" for `qdrant.exe`. It is built in, but its crash restart is weaker.
- Rancher Desktop also runs k3s/traefik on 80/443. Is anything else on CTRL using it? If not, it can be uninstalled
  after step 6.
- Setting a service's account or recovery, and adding firewall rules, need an elevated shell on CTRL. These are
  the owner's steps (the auto-mode classifier denies host changes from the agent).
