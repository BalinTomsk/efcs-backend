# news_search

Fan-out news search for the FishFind portal. There is one endpoint per search engine, so the page can show
each engine's results as they arrive. Today the only source is `vector` (semantic search via news_idx);
`bm25` and `image` are planned. The SQL keyword search stays on docapi. Contract: `docs/specification.md`.

| Method | Path | Port |
|--------|------|------|
| GET | `/api/v1/newssearch/sources` | 8080 |
| GET | `/api/v1/newssearch/{source}?q=…[&limit=10][&country=CA]` | 8080 |
| GET | `/health` | 8080 |
| GET | `/health/live`, `/health/ready`, `/metrics` | 8081 (never publish) |

```bash
dotnet build NewsSearch.slnx
dotnet run --project NewsSearch.Tests
NEWSIDX_URL=http://localhost:8080 dotnet run --project NewsSearch --no-launch-profile
```
