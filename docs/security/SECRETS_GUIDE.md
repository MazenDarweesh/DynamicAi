# User Secrets & Configuration Security Guide

> [!IMPORTANT]
> Never commit real API keys, database passwords, or connection strings to git (`appsettings.json`, `.env`, or documentation). Use **dotnet user-secrets** locally and **environment variables** / secret stores in Docker and production.

---

## 1. View Current Secrets

```powershell
dotnet user-secrets list --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
```

---

## 2. Configure Gemini API Key

```powershell
dotnet user-secrets set "Gemini:ApiKey" "<YOUR_GEMINI_API_KEY>" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
```

---

## 3. Configure Strict API Key Authentication (`Auth:Mode = Strict`)

`appsettings.json` defaults to `Auth:Mode = Strict`. Configure at least one `Integration` key (for ERP callers) and one `Admin` key (for `/api/llm-costs`, `/api/query-cache`, `/api/feedback`, `/api/metrics`, `/api/config`, `/api/maintenance`):

```powershell
dotnet user-secrets set "Auth:Mode" "Strict" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"

dotnet user-secrets set "Auth:ApiKeys:0:Name" "ERP-Integration" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
dotnet user-secrets set "Auth:ApiKeys:0:Role" "Integration" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
dotnet user-secrets set "Auth:ApiKeys:0:Key" "<ERP_API_KEY>" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"

dotnet user-secrets set "Auth:ApiKeys:1:Name" "Admin-Dashboard" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
dotnet user-secrets set "Auth:ApiKeys:1:Role" "Admin" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
dotnet user-secrets set "Auth:ApiKeys:1:Key" "<ADMIN_API_KEY>" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
```

---

## 4. Configure Application & ERP Database Connections

### Application Metadata DB (`AppDbContextConnection` — EF Core logs, feedback, costs)

```powershell
dotnet user-secrets set "ConnectionStrings:AppDbContextConnection" "Server=localhost,1434;Database=DynamicAi_AppDb;User Id=sa;Password=<YOUR_STRONG_SA_PASSWORD>;TrustServerCertificate=True;" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
```

### Target ERP Database (`ConnectionStrings:DefaultConnection` or Dynamic `connectionString` / `tenantKey`)

- **Recommended Multi-Company ERP Flow:** Keep `Integration:AllowLegacyConnectionString = true`, pass the per-company `connectionString` in `POST /AIQuery/integration/directed-page-ask` over **HTTPS**, and ensure the SQL login has **only `db_datareader`** permissions (never `sa` or `db_owner`).
- **Centralized Fixed-Tenant Alternative:** Register named connections under `Integration:TenantConnections:<tenantKey>` and pass `tenantKey` in the request payload.
- **Local Fallback (`DefaultConnection`):**

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=<SQL_SERVER_HOST>;Database=<ERP_DATABASE>;User Id=dynamicai_readonly;Password=<READONLY_PASSWORD>;TrustServerCertificate=True;" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
```

See [ERP_INTEGRATION_MIGRATION_GUIDE.md](../ERP_INTEGRATION_MIGRATION_GUIDE.md) for the T-SQL script to create the `db_datareader` SQL login.

---

## 5. Obsolete Qdrant Secrets

> [!NOTE]
> **Qdrant was removed from the runtime in Phase 4.** Keys such as `Qdrant:Host` and `Qdrant:ApiKey` are no longer used by `DynamicAi.Api`.

---

## 6. Clear All User Secrets

```powershell
dotnet user-secrets clear --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
```

---

## 7. Run API Locally

```powershell
$env:DOTNET_ROLL_FORWARD = "Major"
dotnet run --project "src/DynamicAi.Api/DynamicAi.Api.csproj" --launch-profile http
```
