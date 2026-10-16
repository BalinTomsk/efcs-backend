# news_search (C# / .NET 10) — Agent Context

Fan-out news search for the portal: one endpoint per search engine, so the page shows each engine's results
as they arrive. Today: `vector` and `bm25` (both via news_idx on CTRL). Planned: `image`. **The SQL LIKE search is
not here; it stays on docapi (jnode).** Runs on **csnode** behind cproxy (`/api/v1/newssearch/`).

**Source of truth:** `docs/specification.md`. Session log: `docs/memory.md` (local). History: `CHANGELOG.md`.
Deploy runbook: `docs/do-update.md` (git-ignored).

## Rules

- **DO NOT COMMIT / PUSH / open-merge-close PRs without explicit user permission.**
- **Never call docapi from here.** SQL stays on jnode and the other engines on csnode, so either node can
  fail alone. That independence is the reason this service exists.
- **One source = one endpoint.** Never aggregate sources into one response: the page relies on per-source
  calls to show results progressively, and a slow source must not hold back the others.
- **A failing source is a 503 on its own endpoint, never a 500,** and never an empty 200 that hides the outage.
- **csnode is small** (~1 GB, four .NET services). Keep `--memory 128m`, workstation GC and the 150 MB guard.
  Check `free -m` before deploying. No model, no database here.
- Routes stay under **`/api/v1/newssearch/`**. 8081 is never published.
- Keep docs in sync: code, `docs/specification.md`, and `CHANGELOG.md` under `[Unreleased]`.

## Build & test

```bash
dotnet build NewsSearch.slnx
dotnet run --project NewsSearch.Tests        # TUnit
```
