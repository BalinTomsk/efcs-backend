# Loop Contract - EFCS Backend

Safety guardrails for backend automation loops.

## Stop Conditions

- **Never deploy without explicit user approval** — ask, and wait for the user to approve
- **Stop on test failures** — don't auto-fix; let user decide
- **Stop on Docker/container errors** — don't ignore build failures
- **Stop on database migration errors** — verify schema before proceeding
- **Max 5 retries per task** — infinite loops prohibited

## Tone & Scope

- **No step-by-step narration** — close a task with a short status summary of what changed and what was verified
- **Never commit, push, or open/merge/close a PR without explicit user permission** — make the edits, then stop with that summary (same rule as each service's CLAUDE.md)
- **Test before claiming success** — run unit tests locally
- **Verify against live DB** — don't assume schema matches main
- **No force-push** — always merge or rebase cleanly

## Secrets & Safety

- **Never commit real credentials** — use `.env` (gitignored)
- **No automated prod deployments** — user must approve every step
- **No direct prod DB writes** — test locally first on stub DB
- **Docker images: verify before pushing** — scan for secrets

## Scope for This Project

- **C# .NET 10 code** — microservices, console apps
- **SQL operations** — read-only queries only; DDL/DML after user approval
- **Docker builds & runs** — local testing only
- **Config changes** — local testing; prod deployment requires approval

## When to Loop vs. Single-Shot

**Use a loop when:**
- Iterating fixes (e.g., "fix all test failures")
- Watching for events (e.g., "retry until CI passes")

**Use a single prompt when:**
- One-and-done (e.g., "add this endpoint")
- Output is opaque (e.g., code review findings)

## Database & Schema

- **Read prod schema live before any change** — don't trust main branch migrations
- **Test migrations locally first** — use local MSSQL 2022 with stub data
- **DB workflow authority** — `c:\envoinx\fishfind\envfish-db\CLAUDE.md`; it does not auto-load, so open it
  explicitly before any schema, proc, view, or seed-data change
- **No nested SqlDataReader** — materialize rows first; no MARS
