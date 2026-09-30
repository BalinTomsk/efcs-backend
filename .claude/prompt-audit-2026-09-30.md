# Prompt Audit — efcs-backend

**Run:** 2026-09-30 · `/claude-api prompt-audit` (bare, whole-repo scope) · **nothing applied**

**Host addresses in this file are redacted as `<droplet-ip>`** — `.claude/` is not gitignored and this
repo is public (see finding 7). The literal values are in the working-tree files at the cited lines.

## Stated assumptions (Step 0)

- **Scope:** the whole working directory's prompt surface. The request named no file, so the inventory
  below is the scope.
- **Target model:** **Claude Opus 5** — the current flagship generation, which is what these files are
  consumed by today. This repo contains **no LLM API code at all** (zero hits for
  `anthropic`/`openai`/`langchain`/`gemini`/`mistral`/`ollama`, no model-ID strings, no
  `thinking`/`budget_tokens`/`temperature`/prefill), so there is no in-progress migration to read a
  target from and no Group 1b or API-fossil surface. The prompt surface is entirely **Claude Code agent
  instructions**: `CLAUDE.md`, `SKILL.md`, and `/loop` prompt templates.
- **Provenance:** every prompt file was authored 2026-07-17 → 2026-08-31 by a single author. Nothing
  dates to the pre-thinking era — no `<scratchpad>`, no "think step by step", no retired model names, no
  prefill scaffolding. **This surface has no ancient cruft.** What it has instead is *drift*: rules that
  now contradict each other, facts that have rotted, and a few workarounds for harness behavior that has
  since changed.

## Inventory (Step 1)

| File | Lines | Kind |
|---|---|---|
| `.claude/loops/Contract.md` | 51 | repo-wide loop guardrails |
| `.claude/loops/Prompts/unit-test-sweep.md` | 29 | `/loop` prompt template |
| `.claude/loops/Examples/build-test-deploy.md` | 42 | `/loop` prompt template |
| `service/OWMService/CLAUDE.md` | 83 | service instructions |
| `service/waterapi/CLAUDE.md` | 81 | service instructions (gitignored) |
| `service/waterservice/CLAUDE.md` | 180 | service instructions |
| `service/weather/CLAUDE.md` | 199 | service instructions (gitignored) |
| `service/waterservice/.claude/skills/update-water/SKILL.md` | 280 | deploy skill (gitignored) |
| `service/weather/.claude/skills/update-weather/SKILL.md` | 339 | deploy skill (gitignored) |

Not audited as prompt surface: `docs/specification.md`, `CHANGELOG.md`, `README.md` (reference
documentation, loaded on demand, not instruction text) — except `weather/CHANGELOG.md:203`, which
finding 7 reaches.

## Summary

**22 findings** — 7 high, 12 medium, 3 flag-only. By group: 1a × 4, 1b × 0, 1c × 5, 1d × 3,
Group 2 × 5, Group 3 × 1, Group 4 × 1, plus 4 cross-file contradictions (keep-list #8: duplicates that
actually *disagree*).

The three that matter most:

1. **`Contract.md:16` tells the model not to summarize, and three other files tell it to.** "Terse
   output — no narration or trailing summaries" is a textbook Group 1d update suppressor — tuned against
   models that over-narrated, and current models under-narrate with it present. Worse, both `SKILL.md`
   files end in a mandatory **"Success reporting"** block and all three service `CLAUDE.md` files say
   "make the edits, then **stop with a status summary**." The repo-wide contract forbids exactly what the
   per-service files require.
2. **`Contract.md:17` "Commit-first workflow — never leave uncommitted work" directly contradicts the
   git rule in every service file** (`waterapi:18`, `waterservice:65-67`, `weather:33`: *do not commit
   without explicit user permission*). `build-test-deploy.md:19` then bakes the wrong side into an
   executable step ("Commit changes with `build: [brief]`"). This is not a stylistic overlap — it is a
   safety rule with a live counter-instruction.
3. **`build-test-deploy.md` choreographs infrastructure that does not exist.** No root `Dockerfile`
   (step 3's `docker build -t efcs-backend:latest .` cannot run), no staging environment anywhere in the
   repo or docs, and "deploy via Rancher" names the *local* Docker engine as a deploy target. The real
   path is the two deploy skills → `debian-csnode`. A 12-step script that can't execute past step 3 is
   the purest form of a fossil.

---

## Findings — High confidence

**1. `.claude/loops/Contract.md:16` — update suppressor, contradicted by four other files**

- **Evidence:** `- **Terse output** — no narration or trailing summaries`
- **Pattern:** Group 1d, "Update suppressors written for chatty models"
- **Why obsolete:** The documented row: tuned against models that over-narrated; current models
  under-narrate with these present. And it disagrees with `update-water/SKILL.md:257-271`,
  `update-weather/SKILL.md:304-319`, `waterservice/CLAUDE.md:68`, `weather/CLAUDE.md:33`, which all
  require a closing status summary.
- **Action:** `rewrite` — keep "don't narrate mid-task", drop the ban on the closing summary.

**2. `.claude/loops/Contract.md:17` — contradicts the git rule in every service file**

- **Evidence:** `- **Commit-first workflow** — never leave uncommitted work`
- **Pattern:** keep-list #8 inverse — duplicated rules that actually disagree
- **Why obsolete:** `waterapi:18`, `waterservice:65-67`, `weather:33` all forbid committing without
  explicit permission; the harness default agrees. A model reading both resolves an unpredictable middle.
- **Action:** `rewrite` — restate the permission rule, delete "commit-first".

**3. `.claude/loops/Contract.md:50` — dangling cross-reference**

- **Evidence:** `- **SQL Server QUOTED_IDENTIFIER gotchas** — see CLAUDE.md`
- **Pattern:** Group 2, volatile specifics / Group 1d unenforced
- **Why obsolete:** Verified: there is **no root `CLAUDE.md`**, and `QUOTED_IDENTIFIER` appears nowhere
  in the repo except this line. It points at nothing.
- **Action:** `rewrite` — point at the real authority (`envfish-db\CLAUDE.md`, which the other three
  files already name).

**4. `.claude/loops/Examples/build-test-deploy.md:3,14,20,22,32,42` — choreography for infrastructure that doesn't exist**

- **Evidence:** `docker build -t efcs-backend:latest .` / `ask user "Deploy to staging?"` / `deploy via
  Rancher (or manual if no API)` / `Verify staging endpoint responds` / `No prod deployment; staging only`
- **Pattern:** Group 2, volatile specifics + the recency trap
- **Why obsolete:** Verified: no root `Dockerfile` (all three live in `service/*/`); `staging` appears
  **only** in this file — not in any service doc, appsettings, or CHANGELOG; Rancher Desktop is the local
  Docker engine per both skills, not a deploy target.
- **Action:** `rewrite` — per-service build+test, hand deploy to the skills that own it.

**5. `.claude/loops/Examples/build-test-deploy.md:19` — executable step that violates the repo's git rule**

- **Evidence:** `8. Commit changes with "build: [brief]"`
- **Pattern:** keep-list #8 inverse (disagreeing duplicates)
- **Why obsolete:** Three service files forbid committing without explicit permission; this step commits
  unprompted, every iteration.
- **Action:** `remove`.

**6. `service/weather/CLAUDE.md:52` — stale version pin**

- **Evidence:** ``Live as **10.0.0** on `debian-csnode` (<droplet-ip>) since 2026-08-08;``
- **Pattern:** Group 2, "hardcoded … version numbers; nothing re-checks them"
- **Why obsolete:** Verified: `WeatherService.csproj` is at **10.2.2**; `CHANGELOG.md` records **10.1.1
  deployed to prod 2026-08-11** and a 10.2.0 release. The same file's own line 156 refers to "until
  10.0.1", so it self-contradicts. `weather/CLAUDE.md:15` names `CHANGELOG.md` as the single source of
  truth for release notes — this line copies it anyway, which `waterapi/CLAUDE.md:22` explicitly forbids
  ("never copied from another doc").
- **Action:** `rewrite` — stop pinning; defer to `CHANGELOG.md` / csproj.

**7. `service/waterservice/CLAUDE.md:104-106` — unenforced rule, visibly violated in a tracked file**

- **Evidence:** `> **Deployment specifics are deliberately not in this repo.** Host addresses … do not
  reintroduce them here, in `README.md`, or in `CHANGELOG.md`.`
- **Pattern:** Group 1d, "Unenforced instructions: rules no code path, eval, or reviewer checks —
  visibly violated"
- **Why obsolete:** The rule is correct and worth keeping, but nothing enforces it and it has already
  failed: `service/weather/CHANGELOG.md:203` — a **tracked, public** file — contains the droplet
  address. (`waterapi/CLAUDE.md` and `weather/CLAUDE.md` legitimately carry it; both are gitignored,
  verified via `git check-ignore`.)
- **Action:** `remove` the leaked address from the tracked changelog, and `add` the enforcement the prose
  is standing in for (a pre-commit grep) — per the documented fix, "enforce in code what can be enforced
  in code."

---

## Findings — Medium confidence

**8. `service/waterservice/CLAUDE.md:155-159` — the same rule twice in one file**

- **Evidence:** a second `## Keeping docs in sync — IMPORTANT` section restating lines 41-57.
- **Pattern:** Group 1c padding — "repetition as reinforcement… duplicated rules make the model spend
  effort reconciling wordings". (Not keep-list #10: that allows *one* end-of-prompt recap, not a
  duplicate heading mid-file.)
- **Action:** `remove` the second copy.

**9. `service/waterservice/CLAUDE.md:61` — emphasis-only heading**

- **Evidence:** `##IMPORTANT` (also malformed — no space after `##`, so it doesn't render as a heading)
- **Pattern:** Group 1a, "emphasis with no adjacent because"
- **Why obsolete:** It labels a grab-bag of unrelated rules with volume instead of a name; when several
  things are marked important the marker stops carrying information.
- **Action:** `rewrite` to a descriptive heading.

**10. `service/waterservice/CLAUDE.md:65-67` — one constraint split into three shouted lines**

- **Evidence:** `**DO NOT COMMIT…**` / `**DO NOT PUSH…**` / `**DO NOT CREATE, MERGE, OR CLOSE PULL REQUESTS…**`
- **Pattern:** Group 1a, "`IMPORTANT: NEVER do X` (several per prompt) → state the one or two real
  constraints plainly"
- **Why obsolete:** The constraint is real and stays (Group 1e: it encodes a policy). Its *volume* is
  dated — `waterapi:18` and `weather:33` already express the identical rule in one plain line each.
- **Action:** `rewrite` to one line, keeping the follow-through already at line 68.

**11. `service/waterservice/CLAUDE.md:71-74` — workaround for skill resolution the harness now does itself**

- **Evidence:** ``Local project skills live under `.claude/skills` … you MUST first look for and use
  `.claude/skills/<skill-name>/SKILL.md`. Only search repo-level `Skills` directories or global skill
  registries if that project-level file does not exist.``
- **Pattern:** Group 1d fossil + Group 3 "prose lists that shadow the real tool list → delete"
- **Why obsolete:** Observed during the audit session: opening a file under `service/waterservice/`
  caused the harness to surface `update-water` automatically, directory-scoped, with its description. The
  lookup this paragraph instructs is no longer the model's job, and "global skill registries" describes
  no current mechanism.
- **Action:** `remove`.

**12. `service/waterservice/CLAUDE.md:83-88` — prose restating the skill's own frontmatter**

- **Evidence:** ``- Use `update-water` when asked to deploy/update/release … - A version tag is
  required. If the user does not provide one, ask for it…``
- **Pattern:** Group 3, duplicated tool list; Group 2, trigger-case enumeration
- **Why obsolete:** The trigger list and the tag requirement are already in `update-water`'s frontmatter
  `description` (which rides in the request) *and* in `SKILL.md:18`. Stated three times; the two copies
  here are the ones that will drift.
- **Action:** `rewrite` — keep only the fact the skill doesn't carry (keep `docs/do-update.md` aligned
  before deploying).

**13. `service/waterapi/CLAUDE.md:44-49` vs `:75-81` — disagreeing status**

- **Evidence:** line 47: *"is reached through cproxy 0.18.0 over VPC peering (nyc1 ↔ tor1)"* vs line 77:
  *"**Transport csnode ↔ cproxy: VPC peering, decided 2026-09-23.** It waits on the user creating the
  peering in the DigitalOcean control panel"*, and line 71: *"the choice is still open"*.
- **Pattern:** keep-list #8 inverse — duplicates that actually disagree
- **Why obsolete:** One section says peering is live and carrying traffic; the other says it's pending
  and the binding choice is unresolved. Both were true at different times.
- **Action:** `rewrite` — state the live topology once; keep only genuinely open items under "Open
  decisions".

**14. `service/waterservice/CLAUDE.md:142` and `service/waterapi/CLAUDE.md:41` — rotted test counts**

- **Evidence:** `# 57 TUnit tests` / `# 28 TUnit tests`
- **Pattern:** Group 2, volatile specifics
- **Why obsolete:** Verified by counting attributes: waterservice has **59** `[Test]`; waterapi has
  **20** `[Test]` + **12** `[Arguments]` cases ≈ **32**. Both already wrong, and wrong again on the next
  test added. The number changes no decision.
- **Action:** `remove` the counts, keep the commands.

**15. `update-weather/SKILL.md:157` vs `:331` — the executable step contradicts the stated preference**

- **Evidence:** Step 8 runs `-p 8081:8081`; Notes say *"It is bound to `0.0.0.0` with no firewall on
  these droplets, so the health endpoint is publicly reachable; `-p 127.0.0.1:8081:8081` is preferable
  unless something off-box polls it."*
- **Pattern:** keep-list #8 inverse (disagreeing duplicates)
- **Why obsolete:** The model runs Step 8 verbatim, so the skill ships the form its own Notes call worse.
  Decide once, in the command.
- **Action:** `rewrite` — pick the binding in Step 8; leave the reason in Notes.

**16. `update-water/SKILL.md:20` and `update-weather/SKILL.md:23-26` — authorization scope stated three ways**

- **Evidence (water):** `proceed through the workflow without asking for additional per-step permission.
  Do not ask for confirmations unless an operation fails. Only ask the user again if a required
  secret/input is missing, a command fails, or continuing requires a decision not covered by this skill.`
- **Pattern:** Group 1c, "say it once, in the right place"
- **Why obsolete:** Three sentences for one rule; the second is fully contained in the third. Current
  models read each as separate signal and spend effort reconciling them.
- **Action:** `rewrite` to one sentence (both files).

**17. `update-water/SKILL.md:46, 49, 83, 244, 249` — "continue anyway" stated five times**

- **Evidence:** `the SKILL will continue anyway - the user will handle setting it in their session` /
  `**Note**: The user has the GHCR PAT available … Do not ask for it or stop the workflow…` / `Note: If
  this command fails, continue anyway…` / table rows at 244 and 249.
- **Pattern:** Group 1c, repetition as reinforcement
- **Why obsolete:** Five restatements of one instruction, in a skill that also has a dedicated
  error-handling table — the right single home. (`update-weather` states it twice; still one too many,
  but far less.)
- **Action:** `rewrite` — one line in Secrets, one row in the table; delete the rest.

**18. `.claude/loops/Prompts/unit-test-sweep.md:9-20` — step choreography over a deterministic plan**

- **Evidence:** 8 numbered steps including `2. Parse test report: count passed, failed, skipped`,
  `7. If YES: ask which test, then loop back to step 1`, plus `8. After 3 loops or user says "done": stop`
  **and** `Max iterations: 3`.
- **Pattern:** Group 1c step-by-step choreography + Group 4 "an LLM executor for a deterministic plan"
- **Why obsolete:** Two documented problems. Prescriptive scripts written for prior models degrade output
  quality on current ones — the model's own plan beats a hand-written one for "run tests and report". And
  running the Group 4 count: steps 1-5 are two fixed `dotnet` commands and a tally, fully determined by
  their inputs; only "which failure to fix" is adaptive. The iteration cap is also stated twice.
- **Action:** `rewrite` — state the outcome, name the one adaptive step, state the cap once.

**19. `update-water/SKILL.md:179, 187, 193, 198` — emphasis density in the verification step**

- **Evidence:** `This is the critical verification step` / `Must see ALL of these` / `Must NOT see` /
  `**Critical**: BOTH the CA worker AND the US worker must each process at least one station`
- **Pattern:** Group 1a, "when several instructions are each marked critical, the markers stop carrying
  information"
- **Why obsolete:** The success criteria themselves are load-bearing contract and **stay** (keep-list
  #4). The four stacked emphasis markers around them are the dated part — `update-weather:194` does the
  same job with one plain sentence ("This is the critical step").
- **Action:** `rewrite` — keep every criterion, state it once at normal volume.

**20. `.claude/loops/Contract.md:5, 8` — emphasis parenthetical + restatement of a harness default**

- **Evidence:** `## Stop Conditions (Non-Negotiable)` and `- **Stop on permission denial** — never retry
  or work around`
- **Pattern:** Group 1a (emphasis with no reason); Group 3 deletion rule (restatement of a
  trained/harness default)
- **Why obsolete:** "Non-Negotiable" adds volume, not information, to a list already headed "Stop
  Conditions". Permission-denial handling is already the harness's own stated rule ("a denied call means
  the user declined it — adjust, don't retry verbatim").
- **Action:** `remove` both. (Low-risk either way — keeping line 8 as belt-and-braces is a reasonable
  decline.)

**21. `.claude/loops/Contract.md:7` — literal-token instruction**

- **Evidence:** `- **Never deploy without explicit user approval** — ask first, wait for "yes"`
- **Pattern:** Group 1c over-specification (method, not goal)
- **Why obsolete:** Current models follow instructions more literally. `wait for "yes"` specifies a
  token, so "go ahead", "ship it", or "do it" can read as not-approval — the opposite of the intent. The
  constraint stays; the method goes.
- **Action:** `rewrite` — "wait for the user to approve".

**22. `.claude/loops/Examples/build-test-deploy.md:20, 25, 34-36` — three conflicting termination specs**

- **Evidence:** `9. After 3 successful cycles…` / `Max iterations: 5` / `## Stop Condition — User says
  "done" or breaks loop with Ctrl+C`
- **Pattern:** Group 1d patch accretion (narrow conditionals that fail unpredictably between them)
- **Why obsolete:** Three different answers to "when does this stop" in a 42-line file. Also disagrees
  with `Contract.md:12` ("Max 5 retries per task").
- **Action:** `rewrite` — folded into finding 4's hunk; one cap, stated once.

---

## Findings — Flag only (no edit proposed)

**23. `.claude/loops/Contract.md:36-44` — "When to Loop vs. Single-Shot".** Matches Group 1c's
strategy-coaching row ("if removing the sentence wouldn't change what is legal or how success is
measured, it's strategy"). Flagged rather than cut because this reads as a human-facing reference section
in a contract file, and the loop/single-shot choice is genuinely the *user's*, not the model's. Low
confidence — your call.

**24. Both `SKILL.md` frontmatter `description` fields enumerate near-synonymous triggers**
(`update-water:3`: "says /update_water, asks to deploy/update/release a water service version, build and
ship a tag, update the droplet, install the latest tagged image, or verify the deployed water service").
Group 2 calls this trigger-case enumeration — but Group 3's deliberate split says routing text may
legitimately carry calibrated urgency, and skills currently under-trigger. At ~45 words these are within
reason. No edit; revisit only if you build a trigger eval.

**25. `update-water/SKILL.md:44-49, 83` — substantive, not dated.** "If GHCR login fails, continue
anyway" means Step 4's `docker push` is guaranteed to fail with a less legible error. That's a product
judgment about your own session setup, outside this audit's remit. Finding 17 consolidates *where* it's
said without changing *what* it says.

## Explicitly not flagged

- **`service/OWMService/CLAUDE.md` (all 83 lines) is clean.** Dense, reason-carrying, zero pressure
  language. The one grep hit (`(NOT SDK-style)`) is a factual correction about the build system, not
  emphasis. Line 69's "even though `readme.md` / `Docs/specification.md` describe older station-query
  variants" reads as migration-relative phrasing but is load-bearing — it warns about a real doc conflict.
- **`weather/CLAUDE.md:71-189` ("Deliberate deviations", "Gotchas") — 119 lines, all kept.** This is
  keep-list #1 in its purest form: every entry carries the *reason* behind a constraint, most traced to a
  specific observed failure (the 2,219 silently-404'd stations, the 3,050 burned station-slots, the
  phantom-crash mechanism). A length-driven audit would cut exactly this. Don't.
- **`update-weather/SKILL.md:50-61` and `:146`** — "Two things this service will not tolerate" plus its
  point-of-use restatement at Step 8. Reads like a prohibition cluster; is actually Group 1e's keeper
  case (each prohibition names its consequence) and a well-placed recap at the exact step where it
  matters.
- **Every `enc:v1:` / master-key / `InvariantGlobalization` / no-force-push / secrets rule** — fragile
  operations with exactly one safe sequence (keep-list #3).

## Group 4 — nothing to report beyond finding 18

No Claude API call sites, so: no API fossils, no cache-hostile prefix ordering, no budget countdowns, no
sub-agent roster. **Token accounting is not applicable** — there is no metered LLM surface in this repo.

---

# Proposed diff

High- and medium-confidence findings only, one finding per hunk. **Nothing applied** — the request was a
bare `prompt-audit`.

### Finding 1 + 2 + 20 + 21 — `.claude/loops/Contract.md`

```diff
@@ -3,12 +3,11 @@
 Safety guardrails for backend automation loops.

-## Stop Conditions (Non-Negotiable)
+## Stop Conditions

-- **Never deploy without explicit user approval** — ask first, wait for "yes"
-- **Stop on permission denial** — never retry or work around
+- **Never deploy without explicit user approval** — ask, and wait for the user to approve
 - **Stop on test failures** — don't auto-fix; let user decide
 - **Stop on Docker/container errors** — don't ignore build failures
 - **Stop on database migration errors** — verify schema before proceeding
 - **Max 5 retries per task** — infinite loops prohibited

 ## Tone & Scope

-- **Terse output** — no narration or trailing summaries
-- **Commit-first workflow** — never leave uncommitted work
+- **No step-by-step narration** — close a task with a short status summary of what changed and what was verified
+- **Never commit, push, or open/merge/close a PR without explicit user permission** — make the edits, then stop with that summary (same rule as each service's CLAUDE.md)
 - **Test before claiming success** — run unit tests locally
```

### Finding 3 — `.claude/loops/Contract.md`

```diff
@@ -48,4 +48,5 @@
 - **Read prod schema live before any change** — don't trust main branch migrations
 - **Test migrations locally first** — use local MSSQL 2022 with stub data
-- **SQL Server QUOTED_IDENTIFIER gotchas** — see CLAUDE.md
+- **DB workflow authority** — `c:\envoinx\fishfind\envfish-db\CLAUDE.md`; it does not auto-load, so open it
+  explicitly before any schema, proc, view, or seed-data change
 - **No nested SqlDataReader** — materialize rows first; no MARS
```

### Finding 4 + 5 + 22 — `.claude/loops/Examples/build-test-deploy.md`

```diff
@@ -1,42 +1,38 @@
 # Loop: Build, Test, Deploy

-Build Docker image, run tests, deploy to staging.
+Build and test one service, then stop for a deploy decision.

 ## Prompt

 ```
 /loop
-Build, test, and deploy EFCS backend.
+Build and test one EFCS backend service.
+
+Service directory: `service/<waterservice|weather|waterapi>` — ask if not stated.

 Each iteration:
-1. Check git status for uncommitted .cs/.yml changes
-2. If none, wait and check again
-3. Build Docker image: `docker build -t efcs-backend:latest .`
-4. If build fails: show error, stop immediately
-5. Run unit tests in container: `docker run ... --entrypoint="dotnet test"`
-6. If tests fail: show output, ask user "Fix and retry?" — STOP if NO
-7. Push image to registry if approved by user
-8. Commit changes with "build: [brief]"
-9. After 3 successful cycles, ask user "Deploy to staging?" — STOP if NO
-10. On approval: deploy via Rancher (or manual if no API)
-11. Verify staging endpoint responds (smoke test)
-12. Stop
+1. `dotnet build`, then `dotnet run --project <Service>.Tests`
+2. On a build or test failure: show the failing output and stop — do not auto-fix
+3. On success: `docker build -t <service>:dev .` in that same directory
+4. Report build + test status and ask whether to deploy
+
+Deploying is out of scope here. On approval, hand off to the service's deploy
+skill (`update-water` / `update-weather`), which owns tagging, GHCR, the droplet,
+and verification. There is no staging environment.

 Max iterations: 5
 ```

 ## When to Use

 - Iterating on backend fixes with immediate feedback
 - Testing Docker builds locally before pushing
-- Validating unit tests before staging deploy
+- Validating unit tests before a tagged deploy

 ## Stop Condition

-User says "done" or breaks loop with Ctrl+C.
+Max iterations reached, a build or test fails, or the user says "done".

 ## Notes

 - Requires local Docker (Rancher Desktop on Windows)
-- Tests run inside container (isolated environment)
-- No prod deployment; staging only
+- Tests run on the host via `dotnet`; the container build is a separate check
+- This loop never deploys — the deploy skills do
```

### Finding 6 — `service/weather/CLAUDE.md`

```diff
@@ -50,7 +50,8 @@
 ## Deployment status

-Live as **10.0.0** on `debian-csnode` (<droplet-ip>) since 2026-08-08; container
+Live on `debian-csnode` (<droplet-ip>); container
 `weather-station-pusher-cs`, port 8081, state on `volume-env` at `/mnt/volume_env/weatherservice`.
+The deployed version is the newest released heading in `CHANGELOG.md` — do not pin it here.
 Three workers enabled (Weather.gov, Open-Meteo, Weather Canada); Visual Crossing and Google Weather are
```

### Finding 7 — `service/weather/CHANGELOG.md` (tracked, public; host address removed)

```diff
@@ -203,3 +203,3 @@
-**Deployed to prod 2026-08-08** — `debian-csnode` (<droplet-ip>), image
+**Deployed to prod 2026-08-08** — `debian-csnode`, image
 `ghcr.io/balintomsk/weather-station-pusher-cs:10.0.0`, port 8081, state on `volume-env` at
```

Plus the enforcement the prose was standing in for — new file `.githooks/pre-commit` (wire with
`git config core.hooksPath .githooks`):

```sh
#!/bin/sh
# Host addresses must not enter this public repo (waterservice/CLAUDE.md § Project identity).
if git diff --cached -U0 | grep -nE '^\+.*\b([0-9]{1,3}\.){3}[0-9]{1,3}\b' | grep -v '127\.0\.0\.1\|0\.0\.0\.0'; then
    echo "pre-commit: a host address is being added to a tracked file. Put it in a gitignored doc." >&2
    exit 1
fi
```

### Finding 8 — `service/waterservice/CLAUDE.md` (duplicate section)

```diff
@@ -153,9 +153,4 @@
 `libicu76` is installed in the image).

-## Keeping docs in sync — IMPORTANT
-
-`docs/specification.md` must always reflect the current state of the code. Treat every source change as two
-steps: ① change the code, ② update `docs/specification.md` (and this file / `docs/do-update.md` if behavior,
-structure, or deployment changed).
-
 ## Secrets
```

### Finding 9 + 10 + 11 — `service/waterservice/CLAUDE.md`

```diff
@@ -59,18 +59,10 @@
 ---

-##IMPORTANT
-Explicitly follows database schema at:
+## Git and database
+
+Database schema lives at:
 - @srv/../../envfish-db

-- **DO NOT COMMIT without explicit user permission.**
-- **DO NOT PUSH without explicit user permission.**
-- **DO NOT CREATE, MERGE, OR CLOSE PULL REQUESTS without explicit user permission.**
-- When code changes are requested, make the file edits and stop with a status summary unless the
-  user explicitly asks for Git actions.
-
-- Local project skills live under `.claude/skills` inside this service. When the user asks to run or
-  use a skill by name, you MUST first look for and use `.claude/skills/<skill-name>/SKILL.md`.
-  Only search repo-level `Skills` directories or global skill registries if that project-level file
-  does not exist.
+- **Do not commit, push, or create/merge/close pull requests without explicit user permission.** Make
+  the file edits, then stop with a status summary unless the user asks for Git actions.

 - **Before making ANY database change** (schema, stored proc, function, view, seed data, or any
```

### Finding 12 — `service/waterservice/CLAUDE.md`

```diff
@@ -83,7 +83,5 @@
-## Local Claude skills
+## Deployment

-- Deployment skill: `.claude/skills/update-water/SKILL.md`
-- Use `update-water` when asked to deploy/update/release `water-station-pusher`, build and push a tagged Docker image, install it on the DigitalOcean droplet, or verify the deployed service.
-- A version tag is required. If the user does not provide one, ask for it before running deployment commands.
-- The deployment runbook/source of truth is `docs/do-update.md`; keep it aligned with the skill before deploying.
+- The `update-water` skill deploys this service; its own description covers when it applies.
+- `docs/do-update.md` is the runbook and the source of truth — keep it aligned with the skill before deploying.
```

### Finding 13 — `service/waterapi/CLAUDE.md` (reconcile peering status)

```diff
@@ -68,12 +68,10 @@
 - Never publish waterapi to the internet directly. Bind it where only cproxy can reach it: the VPC address
-  once the csnode and cproxy VPCs are peered (recommended). Otherwise use a public port firewalled to
-  cproxy's IP only; the choice is still open, see `docs/do-update.md`.
+  (csnode ↔ cproxy VPCs are peered, nyc1 ↔ tor1). Never a public port.
 - waterapi compresses responses and answers ETag 304s. cproxy 0.18.0 had to be fixed for both, so it
   relays compressed bodies untouched and returns a 304 at once. Do not put waterapi behind an older cproxy.

 ## Open decisions

-- **Transport csnode ↔ cproxy: VPC peering, decided 2026-09-23.** It waits on the user creating the
-  peering in the DigitalOcean control panel. waterapi then binds localhost plus its VPC address only.
 - **Frontend switch:** `Editor/ViewMap.aspx.cs` still queries `vMapView` directly (with `state`
```

*If peering is in fact still pending, invert this hunk instead — remove the live claim at line 47.
Either way the file must stop asserting both.*

### Finding 14 — test counts, two files

```diff
--- a/service/waterservice/CLAUDE.md
+++ b/service/waterservice/CLAUDE.md
@@ -140,3 +140,3 @@
 dotnet build
-dotnet run --project WaterService.Tests        # 57 TUnit tests
+dotnet run --project WaterService.Tests        # TUnit
```

```diff
--- a/service/waterapi/CLAUDE.md
+++ b/service/waterapi/CLAUDE.md
@@ -40,3 +40,3 @@
 dotnet build WaterApi.slnx
-dotnet run --project WaterApi.Tests        # 28 TUnit tests
+dotnet run --project WaterApi.Tests        # TUnit
```

### Finding 15 — `update-weather/SKILL.md:157` (bind to loopback, per the file's own Notes)

```diff
@@ -157,1 +157,1 @@
-ssh root@<droplet-ip> "docker run -d --name weather-station-pusher-cs ... -p 8081:8081 ghcr.io/balintomsk/weather-station-pusher-cs:$env:TAG"
+ssh root@<droplet-ip> "docker run -d --name weather-station-pusher-cs ... -p 127.0.0.1:8081:8081 ghcr.io/balintomsk/weather-station-pusher-cs:$env:TAG"
```

```diff
@@ -328,4 +328,4 @@
 - **Port**: 8081 is the only HTTP surface and it *is* published. Nothing listens on 8080. This differs
   from `water-station-pusher-cs`, which publishes 8080 and keeps 8081 private — the two do not clash.
-  It is bound to `0.0.0.0` with no firewall on these droplets, so the health endpoint is publicly
-  reachable; `-p 127.0.0.1:8081:8081` is preferable unless something off-box polls it.
+  Step 8 binds it to loopback: there is no firewall on these droplets, so `0.0.0.0` would expose the
+  health endpoint publicly. If something off-box must poll it, change Step 8 deliberately.
```

> **Dependency to check before taking this hunk:** Step 9's verification `curl`s run *over ssh on the
> droplet itself*, so loopback binding does not break them. Confirm nothing external polls `:8081` first.

### Finding 16 — authorization scope, both skills

```diff
--- a/service/waterservice/.claude/skills/update-water/SKILL.md
+++ b/service/waterservice/.claude/skills/update-water/SKILL.md
@@ -20,1 +20,2 @@
-After the user provides the version tag, proceed through the workflow without asking for additional per-step permission. Do not ask for confirmations unless an operation fails. Only ask the user again if a required secret/input is missing, a command fails, or continuing requires a decision not covered by this skill.
+The tag is authorization for the whole workflow: run it straight through, and ask again only if a
+required input is missing, a command fails, or a decision this skill does not cover comes up.
```

```diff
--- a/service/weather/.claude/skills/update-weather/SKILL.md
+++ b/service/weather/.claude/skills/update-weather/SKILL.md
@@ -23,4 +23,2 @@
-After the user provides the version tag, that is full authorization for the entire deploy workflow. Do
-not ask for additional per-step permission for Docker, GHCR, SSH, droplet, container replacement, log
-inspection, or cleanup. Run the workflow straight through. Only ask again if a required secret/input is
-missing, a command fails, or continuing needs a decision this skill does not cover.
+The tag is authorization for the whole deploy: run it straight through, and ask again only if a
+required input is missing, a command fails, or a decision this skill does not cover comes up.
```

### Finding 17 — `update-water/SKILL.md`, GHCR-PAT instruction stated once

```diff
@@ -42,8 +42,7 @@
 ## Secrets

 - GHCR PAT must have `write:packages` and `read:packages` (starts with `ghp_`).
-- Assumes `$env:GHCR_PAT` is already set in the user's PowerShell session.
-- If login fails with "password is empty", the SKILL will continue anyway - the user will handle setting it in their session.
+- Assumes `$env:GHCR_PAT` is already set in the user's PowerShell session. If a GHCR login fails,
+  continue the workflow — the user handles authentication in their own session.
 - Do not print the PAT in final answers or logs unless the command itself necessarily echoes it.
-
-**Note**: The user has the GHCR PAT available in their session. Do not ask for it or stop the workflow if it appears missing - just continue with the commands and they will handle any authentication issues.
```

```diff
@@ -81,4 +80,2 @@
 rdctl shell sh -c "echo $env:GHCR_PAT | docker login ghcr.io -u BalinTomsk --password-stdin"
 ```
-
-Note: If this command fails, continue anyway - the user will handle authentication in their session.
```

*The two error-table rows at 244 and 249 stay — that table is the right single home.*

### Finding 18 — `.claude/loops/Prompts/unit-test-sweep.md`

```diff
@@ -1,29 +1,26 @@
 # Quick Prompt: Unit Test Sweep

-Run all tests, report coverage, find failing tests.
+Run a service's tests with coverage and report what failed.

 ## Prompt

 ```
 /loop
-Run unit tests and report findings.
-
-1. `dotnet test --logger=trx` — run all tests, generate XML report
-2. Parse test report: count passed, failed, skipped
-3. Show failing tests (if any) with error snippets
-4. Run `dotnet test /p:CollectCoverage=true` — coverage report
-5. Show coverage % by class
-6. Ask user: "Fix any failures?" — STOP if NO
-7. If YES: ask which test, then loop back to step 1
-8. After 3 loops or user says "done": stop
+Run the service's tests with coverage, then report: pass/fail/skip counts,
+each failing test with an error snippet, and per-class coverage.
+
+    dotnet test --logger=trx /p:CollectCoverage=true
+
+If anything failed, ask which failure to fix. Fix only that one, re-run, and
+report again. Stop when the user says "done" or the cap is reached.

 Max iterations: 3
 ```
```

> **Group 4 note that goes with this hunk:** everything before "ask which failure to fix" is one
> deterministic command plus a tally. If this loop runs often, a small script that emits the
> counts/coverage table is strictly cheaper, and the model's one genuinely adaptive job is choosing and
> making the fix.

### Finding 19 — `update-water/SKILL.md`, verification step at normal volume (criteria unchanged)

```diff
@@ -177,22 +177,21 @@
 ### Step 10 - Verify startup verification succeeded

-This is the critical verification step. Check logs for startup verification results:
+This is the critical step. Check the logs for startup verification results:

 ```powershell
 ssh root@<droplet-ip> "docker logs --tail 100 water-station-pusher-cs 2>&1 | grep -E 'Startup verification|station.*processed successfully|@l\":\"Error|@l\":\"Fatal'"
 ```

-**Success criteria:**
-
-Must see ALL of these:
+Success requires all four lines — one per station, one per worker, plus the summary. Both the CA and
+the US worker must each process at least one station; if either shows zero, the deploy has not passed.
+
+Expected:
 - `"StartupVerificationStations enabled — processing 2 station(s) for deployment verification: 02HA014, 01646500"`
 - `"Startup verification: station 02HA014 processed successfully."` (CA worker)
 - `"Startup verification: station 01646500 processed successfully."` (US worker)
 - `"Startup verification: SUCCESS — 2 station(s) processed successfully."`

-Must NOT see:
+Any of these means it did not pass:
 - `"Startup verification: FAILED"`
 - Any station with `"was not processed (may not exist or be supported)"`
 - `"@l":"Error"` or `"@l":"Fatal"` in the startup logs

-**Critical**: BOTH the CA worker AND the US worker must each process at least one station. If either worker shows 0 stations processed, the deployment verification has FAILED.
-
 If verification fails or errors are present, get full logs:
```

---

## Before applying (Step 6/7 obligations)

- **Findings 1 and 2 are the two to take first** — they are the only findings where the model currently
  receives contradictory instructions on a safety-relevant axis (commit permission) and an output-shape
  axis (summaries).
- **Finding 7 has an out-of-band dependency:** the address in `weather/CHANGELOG.md` is already in git
  history, so scrubbing the file stops future exposure but does not rewrite the past. Decide whether that
  matters for a public repo before considering history surgery.
- **Nothing in this diff has a test asserting the old behavior** — grepped for the removed strings
  (`QUOTED_IDENTIFIER`, `efcs-backend:latest`, `staging`, `57 TUnit`, `28 TUnit`) and each appears only at
  the site being changed. The one real coupling is finding 15's port binding, noted inline.
- **Re-run this audit at the next model release** — these files are per-model artifacts, and a rule
  that's load-bearing now can be cruft one generation later.
