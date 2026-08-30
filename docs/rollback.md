# IoBuild Rollback — Restore

Rollback restores the legacy microservices topology when cutover verification fails or post-switch smoke fails.

## When to Rollback

- Import verification fails (row count / parity mismatch)
- `POST /api/v1/cutover/stabilize` not reachable or API unhealthy > 5 min
- Smoke `curl -f http://localhost:80/health` fails after switch

Do not attempt forward-fix on the monolith if data parity is broken — restore from backup.

## Prerequisites

- Backups from `docs/cutover.md` step 2 are available and checksummed
- Legacy `microservices/docker-compose.yml` (6 MySQLs + gateway + rabbitmq + redis) is still runnable
- Admin access to host

## Steps

### 1. Freeze monolith (if not already frozen)

```bash
curl -X POST http://localhost:80/api/v1/cutover/freeze -H "Authorization: Bearer $ADMIN_JWT" || true
docker compose -f docker-compose.yml down
```

### 2. Restore legacy databases

```bash
# Bring up legacy MySQLs (empty) first
docker compose -f /path/to/microservices/docker-compose.yml up -d mysql-iam mysql-devices mysql-projects mysql-subscriptions mysql-analytics mysql-profiles
# Wait healthy
docker compose -f /path/to/microservices/docker-compose.yml ps

# Restore each DB
mysql -h mysql-iam -u root -p$DB_PASSWORD iobuild_iam < /backups/iam.sql
mysql -h mysql-devices -u root -p$DB_PASSWORD iobuild_devices < /backups/devices.sql
mysql -h mysql-projects -u root -p$DB_PASSWORD iobuild_projects < /backups/projects.sql
mysql -h mysql-subscriptions -u root -p$DB_PASSWORD iobuild_subscriptions < /backups/subscriptions.sql
mysql -h mysql-analytics -u root -p$DB_PASSWORD iobuild_analytics < /backups/analytics.sql
mysql -h mysql-profiles -u root -p$DB_PASSWORD iobuild_profiles < /backups/profiles.sql

# Alternatively restore monolith pre-import if rolling back within monolith
mysql -h mysql-monolith -u root -p$DB_PASSWORD iobuild < /backups/monolith-pre-import.sql
```

### 3. Re-point entrypoint to gateway

In legacy topology nginx/LB points at `gateway:8080`. Revert DNS or LB:

```bash
# Example: bring up legacy stack
docker compose -f /path/to/microservices/docker-compose.yml up -d --wait
curl -f http://localhost:8080/health
curl -f http://localhost:80/health
```

### 4. Verify restore

```bash
curl -f http://localhost:80/api/v1/projects?builderId=1 -H "Authorization: Bearer $TOKEN"
# Check parity: compare row counts against backup manifest
```

### 5. Stabilize legacy

No explicit stabilize needed on legacy — writes were never blocked there. Ensure `CutoverReadiness.ShouldBlockWrites` is false on next monolith attempt by clearing flag or redeploying.

## Post-Rollback

- Keep monolith `mysql_monolith_data` volume for forensic (do not delete immediately)
- Document root cause and re-attempt cutover only after fix + new backup + successful `docker compose config` and `dotnet test` on monolith
- Notify stakeholders and close maintenance window
