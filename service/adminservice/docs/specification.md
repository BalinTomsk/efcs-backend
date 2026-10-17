# adminservice — specification

Current state of the service, sufficient to recreate it. History is in `../CHANGELOG.md`.

## Purpose

The admin page `fishfind-frontend/aspnet/stats/Default.aspx` lists every backend service:

| service name | machine name | state | start time | last restart | duration | last action time | last action | version |
|---|---|---|---|---|---|---|---|---|
| waterservice | csnode | alive | 10/01/2026 4:55pm | 10/12/2026 2:55am | 4:22 hr | 10/16/2026 4:55pm | station 05BH004 | 10.3.1 |

Services do not write to the database themselves. Each publishes status messages to RabbitMQ; adminservice is the
only writer of `dbo.ServiceStatus`.

```
waterservice ─┐
weather      ─┤
waterapi     ─┤  POST /api/exchanges/%2F/amq.default/publish     POST /api/queues/%2F/…/get
news_idx     ─┼──────────────► fishfind.servicestatus.events ─────────────► adminservice ──► dbo.sp_service_status_report
news_search  ─┤                  (RabbitMQ management HTTP API)                                        │
docapi       ─┤                                                                                         ▼
cproxy       ─┘                                                       stats/Default.aspx ◄── dbo.fn_service_status_list
```

## Message contract — `fishfind.service-status.v1`

```json
{"type":"fishfind.service-status.v1","event":"heartbeat","service":"waterservice","machine":"csnode",
 "version":"10.3.1","processStartUtc":"2026-10-16T12:00:00Z","sentUtc":"2026-10-16T13:05:00Z",
 "lastActionUtc":"2026-10-16T13:04:58Z","lastAction":"station 05BH004"}
```

| field | rule |
|---|---|
| `type` | exactly `fishfind.service-status.v1`, else the message is rejected |
| `event` | `start` (process came up), `heartbeat` (every 60 s), `stop` (clean shutdown) |
| `service`, `machine` | required, trimmed and lower-cased; max 64 / 128 chars |
| `version` | optional, cut to 32 chars |
| `processStartUtc`, `sentUtc` | required ISO-8601; read as UTC when no offset is given |
| `lastActionUtc`, `lastAction` | optional; `lastAction` is ignored without `lastActionUtc`, cut to 256 chars |

Publishers send non-persistent messages (`delivery_mode` 1) with `routing_key` = the queue on the default exchange,
and never declare the queue. `lastAction` must never carry personal data: docapi sends the route pattern, cproxy the
first three path segments, news_search the engine name, never the query.

## Behaviour

- **Queue declare** at startup: durable, `x-message-ttl` = `MessageTtlSeconds`×1000 (600 s),
  `x-max-length` = `MaxQueueLength` (10 000). Retries with back-off (5 s doubling to 60 s) while RabbitMQ is down.
- **Poll** every `PollMilliseconds` (5000): `GET` up to `BatchSize` (100) messages, `ackmode=ack_requeue_false`.
  Messages are parsed; invalid ones are counted `rejected` and logged. Per (service, machine) only the message with
  the newest `sentUtc` is stored (`superseded` for the rest).
- **Store** each kept message with `dbo.sp_service_status_report`. A database error drops the rest of the batch
  (`dropped`) and backs the poll off exponentially up to `MaxBackoffSeconds` (120). Nothing is re-published.
- `start` and `stop` events are logged at Information; heartbeats are not logged.
- **Own row**: `SelfStatusReporter` writes `adminservice` directly through the same procedure — `start`, then a
  `heartbeat` every `HeartbeatSeconds` (60), `stop` on shutdown — with `lastAction` = "N status messages stored".

## Database objects (envfish-db)

- `dbo.ServiceStatus` (`script01_createTable.sql`): `id`, `serviceName`, `machineName` (unique together), `version`,
  `startUtc` (first start ever reported), `restartUtc` (latest process start, NULL until the first restart),
  `lastSeenUtc`, `lastEvent`, `lastActionUtc`, `lastAction`, `updatedUtc`.
- `dbo.sp_service_status_report` (`script02_Proc.sql`): upsert; ignores a message older than `lastSeenUtc`; a
  `processStartUtc` later than the current start becomes `restartUtc`; keeps the newer `lastAction`. Returns `applied`.
- `dbo.fn_service_status_list(@deadAfterSeconds)` (`script02_Funct.sql`): rows plus `state` = `dead` when the last
  event was `stop` or nothing arrived for `@deadAfterSeconds`, else `alive`; `upSeconds` since the latest start for an
  alive service.
- `dbo.sp_service_status_delete(@id)`: the page's Remove button for a retired service.

## Configuration

| key | meaning |
|---|---|
| `DB_URL`, `DB_USERNAME`, `DB_PASSWORD` | SQL Server (JDBC-style URL or native connection string). Unset ⇒ nothing is stored (warning at startup). |
| `RABBITMQ_MANAGEMENT_URL`, `RABBITMQ_USERNAME`, `RABBITMQ_PASSWORD` | all three ⇒ the consumer runs |
| `STATUS_MACHINE` | host name of adminservice's own row (in Docker the machine name is the container id) |
| `AdminService__Status__*` | `Queue`, `PollMilliseconds`, `BatchSize`, `MaxBackoffSeconds`, `MessageTtlSeconds`, `MaxQueueLength`, `HeartbeatSeconds`, `Machine` |
| `FF_MASTER_KEY_FILE` | key for `enc:v1:` values |

Publishers read `RABBITMQ_MANAGEMENT_URL` / `_USERNAME` / `_PASSWORD`, `STATUS_MACHINE` and optional
`STATUS_INTERVAL_SECONDS` (cproxy: `CPROXY_RABBITMQ_*`, `CPROXY_STATUS_MACHINE`, `CPROXY_STATUS_INTERVAL_SECONDS`,
`CPROXY_STATUS_ENABLED`).

## Surfaces

- `8080`: `GET /health` → `{status, version, uptime}` (Docker HEALTHCHECK). Nothing else.
- `8081`: `/health/live` (process only), `/health/ready` (database), `/metrics`
  (`adminservice_status_messages_total{outcome=applied|stale|superseded|rejected|dropped}`).
- Publish neither port.

## Structure

```
AdminService/
  Program.cs                       host, Serilog, port split
  Configuration/                   AdminOptions, ServiceRegistration, DotEnvLoader, SecretCodec, JdbcConnectionString
  Data/                            ISqlConnectionFactory, SqlConnectionFactory, ServiceStatusRepository
  Messaging/RabbitMqManagementClient.cs
  Status/                          ServiceStatusMessage, ServiceStatusConsumer, SelfStatusReporter
  Web/                             AppInfo, DbHealthCheck, Controllers/HealthController
AdminService.Tests/ServiceStatusTests.cs   TUnit: contract parsing, batching, DB failure, machine name
```

## Deployment

Docker on csnode beside waterservice / waterapi / news_search, `--memory 128m --memory-swap 128m --cpus 0.25`,
no published ports, the env file via `DOTENV_PATH`. Prerequisites: the database objects on production first, then
adminservice, then the publishers (each needs the `RABBITMQ_*` and `STATUS_MACHINE` keys in its env file).
