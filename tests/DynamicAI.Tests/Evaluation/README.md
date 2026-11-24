# Evaluation & E2E Verification Suite

This folder contains the offline golden evaluation dataset, deterministic Query IR shadow-mode comparison suite, and pointers to the opt-in live verification gates. All suites run **100% offline by default** (`0` skipped tests, `$0` LLM cost).

---

## 📦 Artifacts & Test Suites

| File | Scope | Default Mode (Offline) |
| :--- | :--- | :--- |
| [golden_cases_page244.json](file:///e:/Mazen/EGYD/DynamicAi/tests/DynamicAI.Tests/Evaluation/golden_cases_page244.json) | **30 curated Page 244 cases** (bilingual Arabic/English: 24 SQL/Analysis, 4 Clarification, 2 Chat). | Validated for uniqueness, intent distribution, and schema compliance. |
| [GoldenDatasetRunnerTests.cs](file:///e:/Mazen/EGYD/DynamicAi/tests/DynamicAI.Tests/Evaluation/GoldenDatasetRunnerTests.cs) | Runs all 24 SQL cases through `SqlGuardPipeline` in **`Strict`** table-whitelist mode and verifies `PageContextFormatter` + `TailConditionBuilder` on the Page 244 fixture. | Executes offline (`SqlGuardPipeline` + invariant checks). Opt-in live Gemini evaluation when `RUN_LIVE_GOLDEN=true` & `GEMINI_API_KEY` are set. |
| [ShadowModeComparisonTests.cs](file:///e:/Mazen/EGYD/DynamicAi/tests/DynamicAI.Tests/Evaluation/ShadowModeComparisonTests.cs) | **50 total evaluation cases** (30 Page 244 golden cases + 20 extended IR cases covering `TOP`, `OFFSET/FETCH`, date operators, Arabic tokenization, `COUNT(*)`, multi-dropdown joins) + `UnifiedQueryHandler` IR routing & fallback tests. | Compiles all 44 SQL/IR cases via `QueryIrCompiler`, runs each through `SqlGuardPipeline` (`Strict`), and asserts `>= 98%` match rate. |
| [LiveApiE2ETests.cs](file:///e:/Mazen/EGYD/DynamicAi/tests/DynamicAI.Tests/Integration/LiveApiE2ETests.cs) | 4-step HTTP integration check: `GET /AIQuery/ping`, `GET /swagger/v1/swagger.json`, `POST /AIQuery/integration/directed-page-ask`, and `GET /api/llm-costs/budget-status` + `recent-logs`. | Verifies Page 244 fixture contract offline by default. Opt-in live HTTP execution when `RUN_LIVE_E2E=true` is set. |

---

## 🚀 Running Offline Evaluation

```powershell
$env:DOTNET_ROLL_FORWARD = "Major"
dotnet test DynamicAi.sln --filter "FullyQualifiedName~DynamicAI.Tests.Evaluation"
```

---

## 🌐 Running Opt-In Live Gates

### 1. Live Golden Gemini Evaluation (`RUN_LIVE_GOLDEN=true`)

Requires **both** `RUN_LIVE_GOLDEN=true` and `GEMINI_API_KEY`:

```powershell
$env:DOTNET_ROLL_FORWARD = "Major"
$env:RUN_LIVE_GOLDEN = "true"
$env:GEMINI_API_KEY = "<YOUR_GEMINI_API_KEY>"
dotnet test DynamicAi.sln --filter "FullyQualifiedName~GoldenDatasetRunnerTests"
```

### 2. Live 4-Step HTTP E2E (`RUN_LIVE_E2E=true`)

Requires `RUN_LIVE_E2E=true`, `E2E_ERP_CONNECTION_STRING` (or `PRODUCTION_DB_CONNECTION_STRING`), and `E2E_API_KEY` (an `Admin` key when `Auth:Mode = Strict` so Step 4 can read `/api/llm-costs/*`):

```powershell
$env:DOTNET_ROLL_FORWARD = "Major"
$env:RUN_LIVE_E2E = "true"
$env:E2E_ERP_CONNECTION_STRING = "Server=<HOST>;Database=<DB>;User Id=<READONLY_USER>;Password=<PASSWORD>;TrustServerCertificate=True;"
$env:E2E_API_KEY = "<YOUR_ADMIN_API_KEY>"
# Optional: $env:E2E_BASE_URL = "http://localhost:5053"
dotnet test DynamicAi.sln --filter "FullyQualifiedName~LiveApiE2ETests"
```
