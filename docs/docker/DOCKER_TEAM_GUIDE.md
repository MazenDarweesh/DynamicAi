# 🐳 DynamicAi v2.0 Docker Team & Deployment Guide

> [!IMPORTANT]
> Never hardcode real passwords, database IPs, or Gemini/API keys inside Compose files or documentation. Always supply secrets via `.env` (gitignored) or your secret manager.

---

## 📋 Current Compose Architecture (`docker-compose.yml`)

Since Phase 4, **Qdrant has been completely removed** from the DynamicAi runtime. The root [docker-compose.yml](file:///e:/Mazen/EGYD/DynamicAi/docker-compose.yml) defines:

- **`dynamicai-app`** (`mazendarweesh/dynamicai:v2.0.0`, built from root [Dockerfile](file:///e:/Mazen/EGYD/DynamicAi/Dockerfile)): Combined .NET 8 API + Angular 21 UI served on port `8080:8080` (`appuser` non-root).
- **`sqlserver`** (`mcr.microsoft.com/mssql/server:2022-latest`): Local SQL Server for `DynamicAi_AppDb` (EF Core logs, feedback, LLM costs) exposed on `1434:1433` with `sqlcmd` healthcheck.
- **`aspire-dashboard`** (`mcr.microsoft.com/dotnet/aspire-dashboard:latest`, optional `--profile observability`): Local OpenTelemetry UI on port `18888` (`http://localhost:18888`) and OTLP gRPC receiver on `4317:18889`.

---

## ⚙️ Step 1: Configure `.env`

Copy `.env.example` to `.env` in the repository root and replace all placeholder values:

```bash
# Local SQL Server container password (used by sqlserver & AppDbContextConnection)
SA_PASSWORD="<STRONG_SA_PASSWORD>"

# Default / fallback target ERP database connection string (use a db_datareader login)
PRODUCTION_DB_CONNECTION_STRING="Server=<SQL_HOST>;Database=<ERP_DB>;User Id=dynamicai_readonly;Password=<READONLY_PASSWORD>;MultipleActiveResultSets=True;TrustServerCertificate=True;"
PRODUCTION_DB_NAME="QualityControl"

# Google Gemini API key
GEMINI_API_KEY="<YOUR_GEMINI_API_KEY>"

# Strict API Key Auth & Dynamic ERP Integration
AUTH_MODE="Strict"
ERP_API_KEY="<STRONG_RANDOM_ERP_API_KEY>"
ADMIN_API_KEY="<STRONG_RANDOM_ADMIN_API_KEY>"
ALLOW_DYNAMIC_CONNECTION_STRING="true"

# Optional: Enable OpenTelemetry export to .NET Aspire Dashboard
Observability__OtlpEnabled="false"
Observability__OtlpEndpoint="http://aspire-dashboard:18889"
```

---

## 🚀 Step 2: Start Containers

```bash
# Standard stack (dynamicai-app + sqlserver)
docker compose up -d

# With .NET Aspire Dashboard (set Observability__OtlpEnabled=true in .env)
docker compose --profile observability up -d
```

---

## 🔧 Operational Notes & Troubleshooting

1. **Non-Root User & Port `8080`:** `Dockerfile` runs as `appuser`, which cannot bind to port `80`. Keep `ASPNETCORE_URLS=http://+:8080` and map host ports (`8080:8080` or `9090:8080` if `8080` is occupied on the host).
2. **Container Healthcheck:** `aspnet:8.0` does not include `curl`. The root `Dockerfile` uses bash `/dev/tcp/127.0.0.1/8080` against `GET /aiquery/ping`.
3. **External Network (Shared Server Deployments):** If deploying alongside an external ERP SQL container on an existing Docker network, configure an external `networks:` block in a Compose override rather than editing tracked secrets.
4. **Logs & Verification:**

```bash
docker compose ps
docker logs dynamicai-app --tail 100 -f
curl http://localhost:8080/AIQuery/ping
```
