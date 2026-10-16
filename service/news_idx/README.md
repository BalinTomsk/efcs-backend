# news_idx

Semantic news search for FishFind: C# / .NET 10, Qdrant, BAAI/bge-small-en-v1.5 (ONNX, in-process).
An editor publishes an article → a RabbitMQ event → news_idx reads the article from MySQL, embeds it and
stores it in Qdrant. Full contract: `docs/specification.md`. History: [`CHANGELOG.md`](CHANGELOG.md).

## Endpoints

| Method | Path | Port | Purpose |
|--------|------|------|---------|
| GET | `/api/v1/newsidx/search?q=…[&limit=10][&country=CA]` | 8080 | articles ranked by meaning |
| GET | `/health` | 8080 | `{ status, version, uptime }` |
| POST | `/admin/sync[?full=true]` | 8081 | index every published article (background) |
| GET | `/admin/sync` | 8081 | sync progress |
| POST | `/admin/index/{newsId}` | 8081 | index one article now |
| GET | `/health/live`, `/health/ready`, `/metrics` | 8081 | private management — do not publish |

## Build, test, run

```bash
dotnet build NewsIdx.slnx
dotnet run --project NewsIdx.Tests
pwsh ./scripts/fetch-model.ps1                 # ~130 MB into models/ (git-ignored)
docker compose up -d qdrant
dotnet run --project NewsIdx --no-launch-profile
```

Copy `.env.example` to `.env` for MySQL (docapi's `MYSQL_NEWS_*` keys), Qdrant and RabbitMQ. Without
`MYSQL_NEWS_URL` there are no articles; without the RabbitMQ keys the event consumer is off.

## Initial load

On startup an empty collection is filled from MySQL automatically (`NewsIdx:Sync:OnStartup=IfEmpty`).
To force a full re-index:

```bash
docker compose run --rm news_idx --sync-full
```

or `POST http://localhost:8081/admin/sync?full=true` on the host.
