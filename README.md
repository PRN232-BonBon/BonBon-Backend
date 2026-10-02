# BonBon Backend

Backend repository for the BonBon PRN232 project.

## Stack

- C# and .NET 8
- ASP.NET Core Web API
- N-Layer Architecture
- PostgreSQL 16
- Docker Compose deployment for the demo VPS

## Projects

- `BonBon.API`
- `BonBon.Business`
- `BonBon.DataAccess`
- `BonBon.Entities`

No business features or database integration are implemented yet. The current API
exposes `GET /health` so the deployment can be verified before feature work begins.

## Demo deployment

The Compose stack is intentionally small for the 2 vCPU / 2 GB RAM demo VPS:

- `postgres`: PostgreSQL 16 Alpine, limited to 512 MB, exposed only on localhost
- `api`: ASP.NET Core 8, limited to 256 MB, exposed on port 8080

Create the deployment environment file and choose a strong database password:

```bash
cp .env.example .env
nano .env
docker compose up -d --build
docker compose ps
curl http://127.0.0.1:8080/health
```

The SQL schema in `database/migrations/001_initial_schema.sql` is applied only when
PostgreSQL initializes an empty `postgres_data` volume. Do not remove that volume on
a running environment unless the database is intentionally being recreated.
