# news_find (C# / .NET 10) — Agent Context

Semantic search over FishFind news: RabbitMQ event → read the article from MySQL `news` → embed with
**bge-small-en-v1.5** (ONNX, in-process) → **Qdrant**. Serves `GET /api/v1/newsfind/search`. Built like
`../waterapi` (same host skeleton, envelope, port split, secrets, logging, Docker base).

**Source of truth:** `docs/specification.md`. Session log: `docs/memory.md`. History: `CHANGELOG.md`.

## Rules

- **DO NOT COMMIT / PUSH / open-merge-close PRs without explicit user permission.**
- **Keep docs in sync.** Every change is ① code, ② `docs/specification.md`; behaviour changes also get a
  `CHANGELOG.md` entry under `[Unreleased]` with the reason.
- **Version numbers come from `NewsFind/NewsFind.csproj`.**
- **Never touch the photo columns.** No query may name `news_photo0..2`; LONGTEXT only in single-row reads by
  primary key. The Winhost host hangs otherwise (see docapi `MySqlNewsQueryRepository`).
- **news_find is read-only on MySQL** and creates no database object. Before any database change read
  `c:\envoinx\fishfind\envfish-db\AGENTS.md`.
- **The database decides, the event only notifies.** Never index an article from event content.
- **Lower-case news ids everywhere** (`NewsIds.Normalize`) — Qdrant keyword match is case-sensitive.
- **Own queue.** `fishfind.news.events` only; never consume `fishfind.account.events` (cproxy's).
- **Never crash-loop.** Qdrant / RabbitMQ unavailable at startup = retry with back-off, not exit. The
  droplets are small and shared.
- Routes stay under **`/api/v1/newsfind/`** — cproxy sends `/api/v1/news/*` to docapi.
- `/admin/*` is management-port only (8081), like `/metrics`. Never publish 8081.
- Changing the model or chunking requires `POST /admin/sync?full=true` (timestamps cannot see it).
- Model files stay out of git (`models/`, fetched by `scripts/fetch-model.ps1`).

## Build & test

```bash
dotnet build NewsFind.slnx
dotnet run --project NewsFind.Tests        # TUnit
```

## Status

0.1.0 **deployed 2026-10-14** on the vector host (Windows Server + Rancher Desktop). Reached **only through
cproxy 0.23.0** (`/api/v1/newsfind/*`, 2026-10-15). The direct port 4324 is firewalled to cproxy's address
and the LAN. Runbook, including the `d.cmd` Docker wrapper that SSH needs:
git-ignored `docs/do-update.md`. The frontend publisher exists but is switched off.
