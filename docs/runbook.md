# IoBuild Runbook — Monolith Operations

This runbook covers day-to-day operation of the consolidated monolith (one backend `iobuild-api`, one frontend, one MySQL).

## Prerequisites

- Docker Engine 24+ and Docker Compose v2
- .NET 9 SDK for local test runs (`DOTNET_ROOT=/home/arroz/.dotnet`)
- Env file (optional): copy `.env.example` → `.env` and set `DB_PASSWORD`, `JWT_SECRET`, `STRIPE_WEBHOOK_SECRET`, `Influx:Url/Org/Bucket/Token` if telemetry is enabled.

## Quick Health Checks

```bash
# Compose health
docker compose ps
docker compose logs iobuild-api --tail 50
curl -f http://localhost:80/health
curl -f http://localhost:80/api/v1/cutover/status

# Direct API (bypass nginx)
curl -f http://localhost:8080/health

# DB connectivity
docker exec iobuild-mysql-monolith mysqladmin ping -h localhost
```

## Common Commands (see also docs/compose.md)

```bash
docker compose up -d --wait          # start core stack
docker compose --profile telemetry up -d   # with influx/mosquitto/simulator
docker compose logs -f iobuild-api
docker compose down -v               # stop and clear volumes (destroys DB)
```

## Environment

| Variable | Purpose | Default |
|---|---|---|
| `ConnectionStrings__IoBuild` | MySQL DSN inside compose | `Server=mysql-monolith;Port=3306;Database=iobuild;User=root;Password=iobuild` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Jaeger OTLP endpoint | `http://jaeger:4317` |
| `Influx:Url/Org/Bucket/Token` | Live energy/status (optional) | empty → graceful fallback |
| `Jwt:Secret` | JWT signing key | dev fallback |

App must start healthy when Jaeger/Influx are absent (observability is best-effort).

## Logs & Tracing

- Jaeger UI (when `--profile observability`): http://localhost:16686
- OpenTelemetry: configured via `ObservabilityExtensions` — no code change needed.

## On-Call Checklist

1. Check `docker compose ps` — all core services healthy
2. `curl /health` must return 200
3. If DB unhealthy: check `mysql_monolith_data` volume, restore from backup (see docs/rollback.md)
4. If API unhealthy: `docker compose logs iobuild-api` → check migration failures
