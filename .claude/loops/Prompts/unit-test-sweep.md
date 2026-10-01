# Quick Prompt: Unit Test Sweep

Run a service's tests with coverage and report what failed.

## Prompt

```
/loop
Run the service's tests with coverage, then report: pass/fail/skip counts,
each failing test with an error snippet, and per-class coverage.

    dotnet test --logger=trx /p:CollectCoverage=true

If anything failed, ask which failure to fix. Fix only that one, re-run, and
report again. Stop when the user says "done" or the cap is reached.

Max iterations: 3
```

## Output

Pass/fail counts, coverage summary, any failing tests highlighted.

## Time

~2 minutes per run (5-10 with fixes).
