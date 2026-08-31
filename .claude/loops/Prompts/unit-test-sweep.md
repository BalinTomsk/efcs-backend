# Quick Prompt: Unit Test Sweep

Run all tests, report coverage, find failing tests.

## Prompt

```
/loop
Run unit tests and report findings.

1. `dotnet test --logger=trx` — run all tests, generate XML report
2. Parse test report: count passed, failed, skipped
3. Show failing tests (if any) with error snippets
4. Run `dotnet test /p:CollectCoverage=true` — coverage report
5. Show coverage % by class
6. Ask user: "Fix any failures?" — STOP if NO
7. If YES: ask which test, then loop back to step 1
8. After 3 loops or user says "done": stop

Max iterations: 3
```

## Output

Pass/fail counts, coverage summary, any failing tests highlighted.

## Time

~2 minutes per run (5-10 with fixes).
