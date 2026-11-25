# 🐳 DynamicAi Docker Maintenance & Configuration Guide

This document explains how the root [Dockerfile](../../Dockerfile) and [docker-compose.yml](../../docker-compose.yml) build, configure, and run DynamicAi v2.0.

---

## 1. Configuration Precedence (.NET 8)

Configurations load in order (later overrides earlier):
1. `src/DynamicAi.Api/appsettings.json` (base defaults, zero secrets)
2. `src/DynamicAi.Api/appsettings.{Environment}.json`
3. **Environment Variables** (via `.env` / Compose `environment:`)

**Double Underscore Rule (`__`):** Nested JSON keys map to `__` in environment variables (e.g., `Gemini:ApiKey` -> `Gemini__ApiKey`, `Auth:Mode` -> `Auth__Mode`, `Auth:ApiKeys:0:Key` -> `Auth__ApiKeys__0__Key`).

---

## 2. Environment Variables (`.env`)

Copy `.env.example` to `.env` (never commit `.env` to source control):

```bash
# System Metadata DB
SA_PASSWORD="<STRONG_SA_PASSWORD>"

# Optional Default ERP Connection (db_datareader login)
PRODUCTION_DB_CONNECTION_STRING="Server=<SQL_HOST>;Database=<ERP_DB>;User Id=dynamicai_readonly;Password=<READONLY_PASSWORD>;MultipleActiveResultSets=True;TrustServerCertificate=True;"
PRODUCTION_DB_NAME="QualityControl"

# Google Gemini
GEMINI_API_KEY="<YOUR_GEMINI_API_KEY>"

# Strict API Key Auth & Dynamic ERP Integration
AUTH_MODE="Strict"
ERP_API_KEY="<STRONG_RANDOM_ERP_API_KEY>"
ADMIN_API_KEY="<STRONG_RANDOM_ADMIN_API_KEY>"
ALLOW_DYNAMIC_CONNECTION_STRING="true"

# Observability (Aspire Dashboard via --profile observability)
Observability__OtlpEnabled="false"
Observability__OtlpEndpoint="http://aspire-dashboard:18889"
```

> **Note:** Qdrant environment variables (`QDRANT_HOST`, `QDRANT_API_KEY`) were retired in Phase 4 when `ExactQueryCacheService` (`IMemoryCache`) replaced Qdrant.

---

## 3. Multi-Stage Dockerfile Breakdown (`Dockerfile`)

| Stage / Instruction | Purpose |
| :--- | :--- |
| `node:20-alpine` (`frontend-build`) | Installs `frontend/DynamicAi.UI` dependencies and builds the Angular 21 app (`dist/DynamicAi.UI/browser`). |
| `mcr.microsoft.com/dotnet/sdk:8.0` (`backend-build`) | Restores `src/DynamicAi.Api/DynamicAi.Api.csproj` and publishes `DynamicAi.Api` in `Release`. |
| `mcr.microsoft.com/dotnet/aspnet:8.0` (`final`) | Copies published .NET binaries + Angular `wwwroot`, creates non-root `appuser`, exposes `8080`, and runs a `/dev/tcp` `HEALTHCHECK` against `GET /aiquery/ping`. |

---

## 4. Compose Services Breakdown (`docker-compose.yml`)

- **`dynamicai-app`** (`mazendarweesh/dynamicai:v2.0.0`): Runs the combined API + UI on `8080:8080`, depends on healthy `sqlserver`, and binds `Auth__Mode`, `Auth__ApiKeys__*`, `Integration__AllowLegacyConnectionString`, `Gemini__ApiKey`, and `Observability__Otlp*`.
- **`sqlserver`** (`mcr.microsoft.com/mssql/server:2022-latest`): Stores `DynamicAi_AppDb` on `1434:1433` using `sqlvolume` with a `sqlcmd` healthcheck.
- **`aspire-dashboard`** (`--profile observability`): Runs `mcr.microsoft.com/dotnet/aspire-dashboard:latest` on `18888:18888` (UI) and `4317:18889` (OTLP gRPC).

> **Legacy Files:** Older Compose files in `infra/docker/` (`docker-compose.yml`, `docker-compose.team.yml`) are kept only as historical references; use the root [docker-compose.yml](../../docker-compose.yml) for all v2.0 deployments.

---

## 5. Common Commands

```bash
# Build & start v2.0 stack
docker compose up -d --build

# Start with OpenTelemetry Aspire Dashboard
docker compose --profile observability up -d

# Tail application logs
docker logs -f dynamicai-app

# Stop containers (preserve sqlvolume)
docker compose down
```
