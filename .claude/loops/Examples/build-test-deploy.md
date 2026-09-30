# Loop: Build, Test, Deploy

Build and test one service, then stop for a deploy decision.

## Prompt

```
/loop
Build and test one EFCS backend service.

Service directory: `service/<waterservice|weather|waterapi>` — ask if not stated.

Each iteration:
1. `dotnet build`, then `dotnet run --project <Service>.Tests`
2. On a build or test failure: show the failing output and stop — do not auto-fix
3. On success: `docker build -t <service>:dev .` in that same directory
4. Report build + test status and ask whether to deploy

Deploying is out of scope here. On approval, hand off to the service's deploy
skill (`update-water` / `update-weather`), which owns tagging, GHCR, the droplet,
and verification. There is no staging environment.

Max iterations: 5
```

## When to Use

- Iterating on backend fixes with immediate feedback
- Testing Docker builds locally before pushing
- Validating unit tests before a tagged deploy

## Stop Condition

Max iterations reached, a build or test fails, or the user says "done".

## Notes

- Requires local Docker (Rancher Desktop on Windows)
- Tests run on the host via `dotnet`; the container build is a separate check
- This loop never deploys — the deploy skills do
