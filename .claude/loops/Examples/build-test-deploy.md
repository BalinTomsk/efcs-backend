# Loop: Build, Test, Deploy

Build Docker image, run tests, deploy to staging.

## Prompt

```
/loop
Build, test, and deploy EFCS backend.

Each iteration:
1. Check git status for uncommitted .cs/.yml changes
2. If none, wait and check again
3. Build Docker image: `docker build -t efcs-backend:latest .`
4. If build fails: show error, stop immediately
5. Run unit tests in container: `docker run ... --entrypoint="dotnet test"`
6. If tests fail: show output, ask user "Fix and retry?" — STOP if NO
7. Push image to registry if approved by user
8. Commit changes with "build: [brief]"
9. After 3 successful cycles, ask user "Deploy to staging?" — STOP if NO
10. On approval: deploy via Rancher (or manual if no API)
11. Verify staging endpoint responds (smoke test)
12. Stop

Max iterations: 5
```

## When to Use

- Iterating on backend fixes with immediate feedback
- Testing Docker builds locally before pushing
- Validating unit tests before staging deploy

## Stop Condition

User says "done" or breaks loop with Ctrl+C.

## Notes

- Requires local Docker (Rancher Desktop on Windows)
- Tests run inside container (isolated environment)
- No prod deployment; staging only
