# IoBuild v2

IoBuild v2 migrates the existing microservice system into a modular monolith with one backend, one frontend, and one SQL database. InfluxDB and the Python simulator remain separate integrations.

The migration is delivered incrementally through stacked branches so each foundation and feature slice can be reviewed and integrated independently.

## Architecture

- **One backend** (`iobuild-api`, ASP.NET Core 9) — modular monolith collapsing 6 microservices (IAM, Devices, Projects, Subscriptions, Analytics, Profiles) into a single `IoBuildDbContext` and one MySQL. No YARP gateway, no RabbitMQ, no Redis. Analytics projections (`device_projection`, `project_projection`, `unit_projection`) are kept as plain tables with LWW import; plumbing (consumers/buses) removed.
- **One frontend** (Vue 3 + Vite, served via nginx) — built from `frontend/Dockerfile`, proxied by `nginx` at `/api/` → `iobuild-api:8080`.
- **One MySQL** (`mysql-monolith`) — single database `iobuild`; migrations consolidated under `backend/src/IoBuild.Api/Persistence`.
- **Optional integrations** (profile-gated): `jaeger` (observability, `--profile observability`), `influxdb` + `mosquitto` + `simulator` (telemetry, `--profile telemetry`), all on `iobuild-network` with volumes `mysql_monolith_data`, `influxdb_data`, `mosquitto_data`.

Entry: `nginx` (80) → `iobuild-api` (8080) or frontend SPA. Health at `/health` and `/api/v1/cutover/status`.

## Quickstart

### Local (without Docker)

```bash
export DOTNET_ROOT=/home/arroz/.dotnet
export PATH=$DOTNET_ROOT:$PATH

dotnet restore backend/IoBuild.sln
dotnet build backend/IoBuild.sln --no-restore
dotnet test backend/IoBuild.sln --no-restore
# filtered
dotnet test backend/tests/Architecture --filter Cleanup --no-restore -v n
```

### Docker Compose

```bash
# Validate
docker compose -f docker-compose.yml config
docker compose --profile telemetry config

# Core stack (backend + frontend + mysql + nginx)
docker compose up -d --wait
curl -f http://localhost:80/health
curl -f http://localhost:80/api/v1/cutover/status

# With observability (jaeger at http://localhost:16686)
docker compose --profile observability up -d --wait

# With telemetry (influx + mosquitto + simulator)
docker compose --profile telemetry up -d --wait
docker compose --profile full up -d --wait   # observability + telemetry

# Logs & lifecycle
docker compose logs -f iobuild-api
docker compose ps
docker compose down
docker compose down -v  # clear DB volume
```

See `docs/compose.md` for service table, networks/volumes, healthchecks, and Dockerfile handling.
See `docs/runbook.md` for health, env, and on-call.
See `docs/cutover.md` for freeze→backup→import→verify→switch→stabilize.
See `docs/rollback.md` for restore.

### Frontend

```bash
cd frontend
npm ci
npm run dev    # Vite on http://localhost:5173 (proxies /api to nginx)
npm run build
```

### Docker Builds

```bash
docker build -f backend/Dockerfile -t iobuild-api:local ./backend
docker build -t iobuild-frontend:local ./frontend
```

## Project Layout

```
backend/                 # .NET 9 monolith (IoBuild.Api)
  src/IoBuild.Api/       # Program.cs, modules, persistence
  tests/                 # Architecture, Contract, Modules, Integration
  tools/                 # IoBuild.Cutover, IoBuild.LegacyImporter
frontend/                # Vue 3 SPA
nginx/nginx.conf         # /api → iobuild-api:8080
docker-compose.yml       # final (promoted from docker-compose.cutover.yml)
docker-compose.cutover.yml # cutover reference
docs/                    # runbook, cutover, rollback, compose
.github/workflows/ci.yml # dotnet test + docker build + compose smoke
```

## CI

`ci.yml` runs on `push`/`pull_request` to `main`:

- `setup-dotnet` 9.0.x
- `dotnet restore` / `dotnet build` / `dotnet test backend/IoBuild.sln`
- `docker build` (backend + frontend)
- `docker compose config` validation
- `docker compose up -d --wait` smoke
- `curl -f http://localhost:80/health` and `curl health` checks
- `docker compose down -v` cleanup

## Cutover Note

Retire topology only after stabilization: `docker-compose.yml` is the promoted cutover. Legacy 6-MySQL + gateway + rabbitmq + redis compose is archived in `microservices/`. Roll back via restore — see `docs/rollback.md`.

## Env

| Key | Example |
|---|---|
| `ConnectionStrings__IoBuild` | `Server=mysql-monolith;Port=3306;Database=iobuild;User=root;Password=iobuild` |
| `Jwt:Secret` | `iobuild-development-secret-must-be-replaced-before-production` |
| `Influx:Url/Org/Bucket/Token` | optional — graceful fallback if empty |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://jaeger:4317` |
