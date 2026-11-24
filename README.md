# DynamicAi v2.0 — Directed-Page SQL & Analytics Engine

**DynamicAi** is a production-hardened, Clean Architecture (.NET 8 + Angular 21) natural-language-to-SQL and predictive analytics engine built for ERP systems. In v2.0, DynamicAi focuses **100% on Directed-Page Integration** (`POST /AIQuery/integration/directed-page-ask`), eliminating runtime RAG/Qdrant vector lookups and anchoring directly to the ERP page metadata supplied per request.

> **Retired Endpoint Notice:** `POST /AIQuery/ask` (legacy RAG path) is retired and returns `410 Gone`. All ERP and POC queries run through `POST /AIQuery/integration/directed-page-ask`.

---

## 🏗️ Architecture Overview (v2.0)

```text
ERP Client / Angular 21 POC UI (frontend/DynamicAi.UI)
  │
  ▼  HTTPS + X-Api-Key (Integration / Admin)
┌──────────────────────────────────────────────────────────────────────┐
│ 1. Middleware & Security Pipeline (src/DynamicAi.Api)                │
│    • CorrelationIdMiddleware (X-Correlation-ID propagation)          │
│    • Strict X-Api-Key Auth (Integration | AdminOnly policies)        │
│    • LlmBudgetLimitMiddleware ($2.00/day hard limit, 60s cache)      │
└──────────────────────────────────┬───────────────────────────────────┘
                                   ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 2. Orchestration & Exact Cache (QueryOrchestrator)                   │
│    • ExactQueryCacheService (IMemoryCache, <5ms hit, Arabic/numeral  │
│      normalization, Tenant/Company/Page isolation, max 10k entries)  │
│    • Bypassed automatically when active conversation history exists  │
│    • Zero Qdrant / Zero runtime Embedding API calls                  │
└──────────────────────────────────┬───────────────────────────────────┘
                                   ▼  (on cache miss)
┌──────────────────────────────────────────────────────────────────────┐
│ 3. Unified Query Handler (UnifiedQueryHandler)                       │
│    • Google.GenAI SDK (0.10.0) + gemini-3.5-flash-lite               │
│    • Native Structured Outputs + SystemInstruction Implicit Caching  │
│    • Optional deterministic QueryIrCompiler & Shadow Mode            │
│      (Features:UseQueryIrCompiler / Features:QueryIrShadowMode)      │
│    • Self-healing SQL retry loop on sanitization/DB errors           │
└──────────────────────────────────┬───────────────────────────────────┘
                                   ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 4. Single-Pass SqlGuardPipeline (TSql160Parser AST)                  │
│    • Read-only AST validation (SecurityVisitor — blocks DML/DDL/EXEC)│
│    • TableWhitelistMode (AuditOnly / Strict)                         │
│    • Mandatory ComID (@CurrentCompanyId) + Scoped TailCondition      │
└──────────────────────────────────┬───────────────────────────────────┘
                                   ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 5. Execution, Forecasting & Observability                            │
│    • SandboxedSqlExecutor: Dapper + Polly v8 (per-connection CB/     │
│      Retry/Timeout) against dynamic ERP ConnectionString             │
│      (db_datareader) or optional TenantKey                           │
│    • PredictionMathService: deterministic linear regression summary  │
│    • OpenTelemetry (DynamicAi source/meter) + Serilog Compact JSON   │
│      with SecretRedactionDestructuringPolicy + .NET Aspire Dashboard │
└──────────────────────────────────────────────────────────────────────┘
```

---

## 🔑 Key Capabilities

- **Directed-Page Integration (`POST /AIQuery/integration/directed-page-ask`):** Accepts `ExplicitContext` (`MasterSetup`, `DetailSetups`, `Columns`, `TailCondition`, `IsVisible`), `ComID`, `TenantId`, and either a dynamic `ConnectionString` (HTTPS + `db_datareader`) or an optional configured `TenantKey`.
- **Strict API Key Authentication & RBAC:** `ApiKeyAuthenticationHandler` (`Auth:Mode = Strict` by default) enforces `X-Api-Key` / `Authorization: Bearer` with `Integration` role for query endpoints and `AdminOnly` policy for management/cost/cache dashboards. `GET /AIQuery/ping` remains anonymous for health checks.
- **Zero-Vector Exact Query Cache:** `ExactQueryCacheService` uses a bounded `IMemoryCache` (`MaxEntries = 10000`, `TTLDays = 7`) with Arabic diacritic/letter/numeral normalization and strict `TenantId` + `CompanyId` + `PageId` scoping (`<5ms` hits, `$0` embedding cost).
- **Optional Deterministic Query IR Compiler:** Feature-flagged (`Features:UseQueryIrCompiler`, `Features:QueryIrShadowMode`) `QueryIrCompiler` compiles structured `QueryIrPlan` JSON into parameterized T-SQL (`@p0`, `@p1`, ...) with automatic `TM`/`TD` and dropdown `T1`/`T2` joins and automatic fallback to `unified_master` raw SQL for advanced queries.
- **LLM Financial Governance:** Real token extraction (`InputTokens`, `OutputTokens`, `CachedTokens`) from `Google.GenAI` `UsageMetadata`, USD cost tracking (`$0.30` / `$2.50` / `$0.03` per 1M tokens for `gemini-3.5-flash-lite`), and `LlmBudgetLimitMiddleware` enforcing `DailyBudgetUsd = 2.00` (with 60s cached budget checks).
- **Observability & Redaction:** OpenTelemetry traces/metrics exported via OTLP to `.NET Aspire Dashboard` (`docker compose --profile observability`), Serilog `CompactJsonFormatter`, and automatic secret redaction in logs.

---

## 📁 Repository Structure

```text
DynamicAi/
├── DynamicAi.sln                        # Main solution (4 src projects + DynamicAI.Tests)
├── Dockerfile                           # Multi-stage build (Angular 21 UI + .NET 8 API)
├── docker-compose.yml                   # dynamicai-app + sqlserver + optional aspire-dashboard
├── .env.example                         # Environment variable template (no secrets)
├── src/
│   ├── DynamicAi.Domain/                # Entities & domain contracts
│   ├── DynamicAi.Application/           # Orchestrator, UnifiedQueryHandler, QueryIrCompiler,
│   │                                    # SqlGuardPipeline, Prompt templates, Telemetry
│   ├── DynamicAi.Infrastructure/        # GoogleGenAiClient, ExactQueryCacheService,
│   │                                    # SandboxedSqlExecutor, TenantConnectionResolver, EF Core
│   └── DynamicAi.Api/                   # Controllers, Middleware, Auth, Serilog/OTel setup
├── tests/
│   └── DynamicAI.Tests/                 # 287 automated tests (Unit, Security Red-Team,
│                                        # Integration, Golden Evaluation & Shadow Mode)
├── frontend/
│   └── DynamicAi.UI/                    # Angular 21 POC Tester & Admin Console
├── tools/
│   └── legacy/                          # Isolated legacy Qdrant schema tools (SchemaTools.sln,
│                                        # SchemaGenerator, SchemaBootstrapper, 10 smoke tests)
├── docs/                                # Architecture, Docker, Security, Testing & Migration guides
└── infra/
    └── docker/                          # Legacy/reference Docker Compose & maintenance docs
```

---

## ⚡ Quick Start

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (if running on .NET 9/10 runtime, set `DOTNET_ROLL_FORWARD=Major`)
- [Node.js 20+](https://nodejs.org/) & npm (for `frontend/DynamicAi.UI`)
- SQL Server (local instance or via `docker compose`)
- Google Gemini API key

### 1. Configure Secrets (Local Development)

Never place real keys in `appsettings.json`. Use `dotnet user-secrets`:

```powershell
dotnet user-secrets set "Gemini:ApiKey" "<YOUR_GEMINI_API_KEY>" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
dotnet user-secrets set "Auth:Mode" "Strict" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
dotnet user-secrets set "Auth:ApiKeys:0:Name" "ERP-Integration" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
dotnet user-secrets set "Auth:ApiKeys:0:Role" "Integration" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
dotnet user-secrets set "Auth:ApiKeys:0:Key" "<YOUR_ERP_API_KEY>" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
dotnet user-secrets set "Auth:ApiKeys:1:Name" "Admin-Dashboard" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
dotnet user-secrets set "Auth:ApiKeys:1:Role" "Admin" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
dotnet user-secrets set "Auth:ApiKeys:1:Key" "<YOUR_ADMIN_API_KEY>" --project "src/DynamicAi.Api/DynamicAi.Api.csproj"
```

See [SECRETS_GUIDE.md](file:///e:/Mazen/EGYD/DynamicAi/docs/security/SECRETS_GUIDE.md) for connection string options.

### 2. Run Backend + Frontend Locally

```powershell
# Terminal 1: Start .NET API (http://localhost:5053)
$env:DOTNET_ROLL_FORWARD = "Major"
dotnet run --project src/DynamicAi.Api/DynamicAi.Api.csproj --launch-profile http

# Terminal 2: Start Angular 21 POC & Admin UI (http://localhost:4200)
npm --prefix frontend/DynamicAi.UI start
```

### 3. Run with Docker Compose

```powershell
cp .env.example .env
# Fill in SA_PASSWORD, GEMINI_API_KEY, ERP_API_KEY, ADMIN_API_KEY, PRODUCTION_DB_CONNECTION_STRING
docker compose up -d

# Optional: include .NET Aspire Dashboard (http://localhost:18888)
docker compose --profile observability up -d
```

### 4. Run Tests

```powershell
$env:DOTNET_ROLL_FORWARD = "Major"

# Main test suite (287 tests: Unit, Security Red-Team, Integration, Evaluation)
dotnet test DynamicAi.sln

# Isolated legacy schema tools smoke tests (10 tests)
dotnet test tools/legacy/SchemaTools.sln
```

---

## 📚 Documentation

- **Frontend UI & POC Console:** [frontend/DynamicAi.UI/README.md](file:///e:/Mazen/EGYD/DynamicAi/frontend/DynamicAi.UI/README.md)
- **ERP Integration & Security Migration:** [ERP_INTEGRATION_MIGRATION_GUIDE.md](file:///e:/Mazen/EGYD/DynamicAi/docs/ERP_INTEGRATION_MIGRATION_GUIDE.md)
- **Secrets Management:** [SECRETS_GUIDE.md](file:///e:/Mazen/EGYD/DynamicAi/docs/security/SECRETS_GUIDE.md)
- **Testing & Opt-In Live Suites:** [TESTING_GUIDE.md](file:///e:/Mazen/EGYD/DynamicAi/docs/testing/TESTING_GUIDE.md) & [Evaluation/README.md](file:///e:/Mazen/EGYD/DynamicAi/tests/DynamicAI.Tests/Evaluation/README.md)
- **Docker Guides:** [DOCKER_TEAM_GUIDE.md](file:///e:/Mazen/EGYD/DynamicAi/docs/docker/DOCKER_TEAM_GUIDE.md) & [DOCKER_MAINTENANCE.md](file:///e:/Mazen/EGYD/DynamicAi/infra/docker/DOCKER_MAINTENANCE.md)

---

## 📄 License

Proprietary software developed by **Dynamic Pro**.
