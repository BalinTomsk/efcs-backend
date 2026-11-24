# csnews-env (C# / .NET 10) — Agent Context

The news service on **csnode** (renamed from `news_search` in 0.6.0). Two surfaces:

1. **Per-engine news search** (`/api/v1/newssearch/*`, public through cproxy): one endpoint per engine so the
   page shows each engine's results as they arrive. Today `vector` and `bm25` (both via news_idx on CTRL).
2. **The news data API** (`/api/v1/csnews/*`, internal, VPC only): every read and write of the MySQL `news`
   database (Winhost). **csnews-env is the only service that talks to that database.** docapi (jnode) keeps
   the public `/api/v1/news/*` routes, their validation, role rules and caches, and calls this API on a miss.

**Source of truth:** `docs/specification.md`. HTTP reference: `docs/api-reference.html`. Session log:
`docs/memory.md`. History: `CHANGELOG.md`. Deploy runbook: `docs/do-update.md`. **The whole `docs/` folder is
git-ignored** (it carries host details); keep it local and up to date.

## Rules

- **DO NOT COMMIT / PUSH / open-merge-close PRs without explicit user permission.**
- **No other service may open a MySQL connection to the news database.** A new news read or write is a new
  `/api/v1/csnews/*` route here plus a docapi repository method that calls it — never a driver in docapi.
  (news_idx on CTRL still reads the table directly; moving it behind this API is an open follow-up.)
- **Never call docapi from here.** The dependency is one-way: docapi → csnews-env.
- **`/api/v1/csnews/` is never routed by cproxy.** It includes writes and trusts its caller; it is reachable only
  on localhost and the VPC address.
- **The Winhost BLOB hazard:** any query that names `news_photo0..2` while materialising more than one row hangs.
  Photo columns only by primary key with `LIMIT 1`; multi-row statements select narrow columns, no window
  functions. Never "simplify" the two-statement search into one windowed query.
- **The app account (`portos`) has no INSERT/UPDATE/CREATE ROUTINE.** Writes go through the `sp_news_*`
  procedures (created from the Winhost control panel; definitions in `envfish-db/mysql/script02_Proc.sql`).
- **Validate SQL against a real engine** (`MySqlEngineTests` + a `mysql:8.0` container). A fake store never
  parses SQL.
- **One search source = one endpoint**; never aggregate. A failing source, or the news database, is a **503**
  on its own endpoint, never a 500 and never an empty 200.
- **csnode is small** (~1 GB, five .NET services). Keep the memory cap, workstation GC and the 150 MB guard.
- Keep docs in sync: code, `docs/specification.md`, `docs/api-reference.html`, and `CHANGELOG.md` under
  `[Unreleased]`. A change to a `/api/v1/csnews/*` shape changes docapi's `Http*Repository` classes too.

## Build & test

```bash
dotnet build CsNews.slnx
dotnet run --project CsNews.Tests        # TUnit; MySqlEngineTests need CSNEWS_TEST_MYSQL
```
