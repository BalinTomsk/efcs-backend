# adminservice

Collects the status of every FishFind backend service from RabbitMQ and stores it in SQL Server for the admin page
`stats/Default.aspx`. See `AGENTS.md` for the rules and `docs/specification.md` for the full description.

```bash
cp .env.example .env          # DB_URL, RABBITMQ_*, STATUS_MACHINE
dotnet run --project AdminService
dotnet run --project AdminService.Tests
```
