# IoBuild Compose — Topology

Final monolith topology is one backend + one frontend + one MySQL. No YARP, no RabbitMQ, no Redis.

## Services

| Service | Container | Purpose | Required |
|---|---|---|---|
| `iobuild-api` | `iobuild-api` | ASP.NET Core 9 monolith (8080) | yes |
| `mysql-monolith` | `iobuild-mysql-monolith` | MySQL 8 (single DB `iobuild`) | yes |
| `frontend` | `iobuild-frontend` | Vue SPA (built via `frontend/Dockerfile`) | yes |
| `nginx` | `iobuild-nginx` | Edge proxy — `/api/` → `iobuild-api:8080`, SPA fallback | yes |
| `jaeger` | `iobuild-jaeger` | OTLP collector (4317/4318, UI 16686) | no — profile `observability` |
| `influxdb` | `iobuild-influxdb` | Telemetry sink (profile `telemetry`) | no |
| `mosquitto` | `iobuild-mosquitto` | MQTT broker (profile `telemetry`) | no |
| `simulator` | `iobuild-simulator` | IoT generator (profile `telemetry`) | no |

Optional services are profile-gated so the core stack (`docker compose up`) works without Influx/Mosquitto/Simulator.

## Files

- `docker-compose.yml` — final promoted compose (from `docker-compose.cutover.yml`)
- `docker-compose.cutover.yml` — preserved cutover reference (nginx now fronts monolith instead of YARP gateway)
- `backend/Dockerfile` — multi-stage `mcr.microsoft.com/dotnet/sdk:9.0` → `aspnet:9.0`, healthcheck `curl /health`
- `frontend/Dockerfile` — `node:20-alpine` build → `nginx:alpine` serve
- `nginx/nginx.conf` — `location /api/` → `http://iobuild-api:8080`, `location /health` proxy, SPA `try_files`

## Quick Start

```bash
# Validate
docker compose -f docker-compose.yml config
DOTNET_ROOT=/home/arroz/.dotnet PATH=$DOTNET_ROOT:$PATH dotnet test backend/IoBuild.sln

# Core
docker compose up -d --wait
curl -f http://localhost:80/health
curl -f http://localhost:80/api/v1/cutover/status

# With observability
docker compose --profile observability up -d --wait
# Jaeger UI at http://localhost:16686

# With telemetry (influx + mosquitto + simulator)
docker compose --profile telemetry up -d --wait
# Or full
docker compose --profile full up -d --wait

# Logs
docker compose logs -f iobuild-api
docker compose logs -f nginx

# Down
docker compose down
docker compose down -v   # also clear DB volume
```

## Networks & Volumes

```yaml
networks:
  iobuild-network:
    driver: bridge

volumes:
  mysql_monolith_data:
  influxdb_data:
  mosquitto_data:
```

All services attach to `iobuild-network`. `mysql_monolith_data` persists DB; others are optional and ephemeral if not profile-enabled.

## Healthchecks

- `mysql-monolith`: `mysqladmin ping`
- `iobuild-api`: `curl -f http://localhost:8080/health` (start_period 40s, curl installed in Dockerfile)
- `influxdb`: `influx ping` (when profile enabled)

Nginx has no healthcheck — it depends on `iobuild-api` and `frontend`.

## Dockerfile Handling

`backend/Dockerfile` exists (multi-stage dotnet 9) and is duplicated at `backend/src/IoBuild.Api/Dockerfile` for backward compatibility with the cutover reference. `docker-compose.yml` points at `backend/Dockerfile`; either location satisfies `docker build -f backend/Dockerfile ./backend` and CI's `docker build -f backend/Dockerfile`.

If `frontend/Dockerfile` or `backend/Dockerfile` is missing, `docker compose config` will fail — recreate via the source in this repo.

## Env

Minimal env for local:

```bash
# .env (optional)
DB_PASSWORD=iobuild
JWT_SECRET=iobuild-development-secret-must-be-replaced-before-production
```

Monolith reads `ConnectionStrings__IoBuild` and `OTEL_EXPORTER_OTLP_ENDPOINT` from compose env.

## CI Integration

See `.github/workflows/ci.yml` — runs `dotnet test`, `docker build`, `docker compose config`, `compose up --wait`, `curl health`, `compose down`.
