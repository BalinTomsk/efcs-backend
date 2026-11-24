# csnews-env

The FishFind news service on csnode (C# / .NET 10), named `news_search` until 0.6.0. It does two things:

- **Per-engine news search** for the portal (`/api/v1/newssearch/*`, public through cproxy): one endpoint per
  engine (`bm25`, `vector`, both via news_idx), so the page shows each engine's results as they arrive.
- **The news data API** (`/api/v1/csnews/*`, internal, VPC only): every read and write of the MySQL `news`
  database. It is the only code on the platform with access to that database; docapi calls it and keeps the
  public `/api/v1/news/*` routes and their caches.

| Method | Path | Port | Who calls it |
|--------|------|------|--------------|
| GET | `/api/v1/newssearch/sources`, `/api/v1/newssearch/{source}?q=…` | 8080 | cproxy (public) |
| GET | `/api/v1/csnews/doc/{id}`, `/export/{id}`, `/list`, `/default`, `/photo/{id}`, `/search`, `/lake/{id}`, `/fish/{id}`, `/tagged`, `/ping` | 8080 | docapi (VPC) |
| POST/PUT | `/api/v1/csnews/doc[/{id}]`, `/admin/draft`, `/admin/publish`, `/admin/{id}/photo/{index}` | 8080 | docapi (VPC) |
| GET | `/health` | 8080 | probes |
| GET | `/health/live`, `/health/ready`, `/metrics` | 8081 (never publish) | Docker, Prometheus |

```bash
dotnet build CsNews.slnx
dotnet run --project CsNews.Tests
MYSQL_NEWS_URL="jdbc:mysql://127.0.0.1:13306/envfish?useSSL=false" MYSQL_NEWS_USERNAME=root MYSQL_NEWS_PASSWORD=probe \
NEWSIDX_URL=http://localhost:4324 dotnet run --project CsNews --no-launch-profile
```

The real-MySQL tests (`MySqlEngineTests`) run when `CSNEWS_TEST_MYSQL` points at a throwaway database loaded
from `envfish-db/mysql/` (see the test's comment).
