# DynamicAi Testing Guide

All automated tests run **100% offline by default** (`$0` LLM cost, zero external database or network dependencies) using **xUnit**, **FakeItEasy**, and **FluentAssertions**.

---

## 🧪 Test Suites

### 1. Main Solution (`DynamicAi.sln`) — 287 Tests

Located in `tests/DynamicAI.Tests/`:

| Category | Path | Coverage |
| :--- | :--- | :--- |
| **Unit** | `Unit/` | `ApiKeyAuthenticationHandler`, `TenantConnectionResolver`, `ExactQueryCacheService`, `QueryOrchestrator`, `UnifiedQueryHandler`, `QueryIrCompiler`, `SqlGuardPipeline`, `SqlValidatorService`, `SqlRewriterService`, `SqlHardeningVisitor`, `SanitizationVisitor`, `SandboxedSqlExecutor`, `DailyBudgetLimitService`, `LlmUsageTracker`, `PredictionMathService`, `ConversationContextService`, `DbSchemaProvider`, `PageContextFormatter`, `PromptTemplateEngine`, `CorrelationIdMiddleware`, `SecretRedactor`, `DynamicAiTelemetry` |
| **Security Red-Team** | `Security/` | `SecurityVisitorTests`, `SecurityVisitorRedTeamTests` (DDL/DML, `EXEC`, `WAITFOR`, `OPENROWSET`, `OPENQUERY`, `DBCC`, `BULK INSERT`, `SET`/`DECLARE`, `SHUTDOWN`, `KILL`, SQL injection vectors) |
| **Integration & E2E** | `Integration/` | `DirectedPagePipelineTests`, `SecurityPipelineTests`, `LiveApiE2ETests` (offline contract check by default; opt-in live HTTP E2E) |
| **Evaluation** | `Evaluation/` | `GoldenDatasetRunnerTests` (30 Page 244 cases), `ShadowModeComparisonTests` (50 total cases: 30 Golden + 20 Extended IR) |

Run all 287 tests:

```powershell
$env:DOTNET_ROLL_FORWARD = "Major"
dotnet test DynamicAi.sln
```

### 2. Legacy Schema Tools (`tools/legacy/SchemaTools.sln`) — 10 Smoke Tests

Located in `tools/legacy/SchemaTools.Tests/`:

```powershell
$env:DOTNET_ROLL_FORWARD = "Major"
dotnet test tools/legacy/SchemaTools.sln
```

### 3. Frontend Production Build Check

```powershell
npm --prefix frontend/DynamicAi.UI run build
```

---

## 🌐 Opt-In Live Verification Tests

The live evaluation and E2E tests stay in offline assertion mode unless explicitly enabled via environment variables:

### Live Golden Dataset Evaluation (`GoldenDatasetRunnerTests`)

Runs representative Page 244 cases against Gemini (`gemini-3.5-flash-lite`):

```powershell
$env:DOTNET_ROLL_FORWARD = "Major"
$env:RUN_LIVE_GOLDEN = "true"
$env:GEMINI_API_KEY = "<YOUR_GEMINI_API_KEY>"
dotnet test DynamicAi.sln --filter "FullyQualifiedName~GoldenDatasetRunnerTests"
```

### Live 4-Step HTTP E2E (`LiveApiE2ETests`)

Tests `GET /AIQuery/ping`, `GET /swagger/v1/swagger.json`, `POST /AIQuery/integration/directed-page-ask`, and `GET /api/llm-costs/budget-status` + `recent-logs`:

```powershell
$env:DOTNET_ROLL_FORWARD = "Major"
$env:RUN_LIVE_E2E = "true"
$env:E2E_ERP_CONNECTION_STRING = "Server=<HOST>;Database=<DB>;User Id=<READONLY_USER>;Password=<PASSWORD>;TrustServerCertificate=True;"
$env:E2E_API_KEY = "<YOUR_ADMIN_OR_INTEGRATION_KEY>"
# Optional: target an already-running server instead of auto-starting local API on port 5053
# $env:E2E_BASE_URL = "http://localhost:5053"
dotnet test DynamicAi.sln --filter "FullyQualifiedName~LiveApiE2ETests"
```

> **Note:** Step 4 of `LiveApiE2ETests` queries `/api/llm-costs/*`, which requires an `Admin` role `X-Api-Key` when `Auth:Mode = Strict`.

See [Evaluation/README.md](../../tests/DynamicAI.Tests/Evaluation/README.md) for details on the Golden and Shadow Mode evaluation datasets.
