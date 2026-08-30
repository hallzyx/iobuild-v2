# IoBuild Cutover — Freeze → Backup → Import → Verify → Switch → Stabilize

This document describes the one-way cutover from the 6-MySQL microservices topology to the single-MySQL monolith.
It mirrors `backend/tools/IoBuild.Cutover` and is 퇴only after stabilization.

## Preconditions

- Maintenance window announced; write traffic drained.
- `docker-compose.yml` (final) validated via `docker compose config`.
- Legacy dump accessible (6 MySQLs collapsed to one) or `IoBuild.LegacyImporter` ready.
- Admin token available (role `Admin`).

## Steps

### 1. Freeze (block writes)

```bash
curl -X POST http://localhost:80/api/v1/cutover/freeze \
  -H "Authorization: Bearer $ADMIN_JWT"
# or via gateway: POST /api/v1/cutover/freeze
# Response: {"status":"frozen"} — subsequent writes return 503 with reason
```

Check status:

```bash
curl http://localhost:80/api/v1/cutover/status
# {"status":"frozen"} or {"status":"ready"}
```

### 2. Backup (legacy + monolith)

```bash
# Legacy (example per microservice MySQL)
mysqldump -h mysql-iam -u root -p$DB_PASSWORD iobuild_iam > /backups/iam.sql
# ... repeat for devices/projects/subscriptions/analytics/profiles
# Monolith (target)
mysqldump -h mysql-monolith -u root -p$DB_PASSWORD iobuild > /backups/monolith-pre-import.sql
```

Store backups off-host and verify checksums.

### 3. Import (collapse + LWW)

Option A — `IoBuild.LegacyImporter` (projected tables with LWW and invalid-ref nulling):

```bash
dotnet run --project backend/tools/IoBuild.LegacyImporter -- \
  --source "Server=legacy-host;Database=..." \
  --target "Server=mysql-monolith;Database=iobuild;User=root;Password=iobuild"
```

Option B — manual SQL import (after collapsing init.sql):

```bash
mysql -h mysql-monolith -u root -p$DB_PASSWORD iobuild < /backups/collapsed.sql
```

Importer guarantees:
- `device_projection` LWW on `LastEventAt`
- `project_projection` / `unit_projection` LWW
- Invalid `ProjectId`/`UnitId` nulled when referenced row missing

### 4. Verify (parity)

```bash
# Row counts
mysql -h mysql-monolith -e "SELECT count(*) FROM device_projection; SELECT count(*) FROM project_projection;"

# API smoke
curl -f http://localhost:80/health
curl -f http://localhost:80/api/v1/projects?builderId=1 -H "Authorization: Bearer $TOKEN"

# Parity harness (if available)
dotnet test backend/tests/Integration --filter Parity
```

Any mismatch → do NOT switch; go to rollback.

### 5. Switch (nginx → monolith)

Cutover is nginx already pointing at `iobuild-api:8080` (not `gateway:8080`). Switch is DNS or LB:

```bash
# Verify nginx conf
cat nginx/nginx.conf | grep proxy_pass
# expected: proxy_pass http://iobuild-api:8080

docker compose up -d --wait
curl -f http://localhost:80/api/v1/cutover/status
```

### 6. Stabilize (re-enable writes)

```bash
curl -X POST http://localhost:80/api/v1/cutover/stabilize \
  -H "Authorization: Bearer $ADMIN_JWT"
# {"status":"ready"}
```

After stabilization the legacy topology is retired. Only then `docker-compose.yml` becomes the final topology (this file). Do not tear down legacy until stabilize succeeds and smoke is green for 30 min.

## Post-Cutover

- Monitor `docker compose logs iobuild-api` for 30 min
- Jaeger: confirm traces reach `jaeger:4317` (optional)
- Influx: enable with `--profile telemetry` if needed
