# Dynamic AI 🧠

> **Natural Language to SQL Generation with Semantic Context, Smart Query Condensing & Caching**

Dynamic AI is an intelligent system designed to bridge the gap between natural language questions and complex database queries. It leverages **Google Gemini** for LLM capabilities and **Qdrant** for vector-based semantic search, allowing users to query their data using everyday language while ensuring high performance through intelligent caching.

---

## 🏗️ Architecture Overview

The system follows **Clean Architecture** principles with a **7-Layer Query Pipeline** for maximum accuracy and context awareness.

```text
     User 
      ↕ 
[DynamicAi UI (Angular)] 
      ↕ 
[DynamicAi API (.NET)] ──────┬─────────────────┐
                             │                 │
            ┌────────────────▼────────────────┐│
            │       Query Orchestrator        ││
            │    (7-Layer Pipeline)           ││
            └───────┬────────────────────────-┘│
                    │                          │
        ┌───────────▼───────────┐              │
        │  🆕 Layer 0.5:        │              │
        │  Query Condenser      │              │
        │  (ActionType Logic)   │              │
        └───────────┬───────────┘              │
                    │                          │
        ┌───────────▼───────────┐              │
        │  Layer 1: Query Cache │              │
        │  (Semantic Similarity)│              │
        └───────────┬───────────┘              │
                    │                          │
        ┌───────────▼───────────┐              │
        │  Layer 2: RAG Context │              │
        │  (Qdrant + Schema)    │              │
        └───────────┬───────────┘              │
                    │                          │
        ┌───────────▼───────────┐              │
        │  Layer 3: SQL Gen     │              │
        │  (Gemini + Prompt)    │              │
        └───────────┬───────────┘              │
                    │                          │
        ┌───────────▼───────────┐              │
        │  Layer 4: Validation  │              │
        │  (Security + Rewrite) │              │
        └───────────┬───────────┘              │
                    │                          │
        ┌───────────▼───────────┐              │
        │  Layer 5: Execution   │              │
        │  (SQL Server)         │              │
        └───────────┬───────────┘              │
                    ↕                          │
             [(SQL DB)]                        │
                                               │
                         ┌─────────────────────▼──┐
                         │   Feedback Service     │
                         │ (Like/Dislike → Golden │
                         │    Dataset Pipeline)   │
                         └────────────┬───────────┘
                                      ↕
                            [TrainingExamples DB]
```

### 🛠️ Technology Stack

*   **Backend:** .NET 8 (C#)
*   **Frontend:** Angular 17+ (TypeScript, SCSS)
*   **Vector Database:** Qdrant (for schema embeddings & query caching)
*   **LLM:** Google Gemini (via Semantic Kernel)
*   **Database:** SQL Server + EF Core (QueryLogs, TrainingExamples)
*   **Containerization:** Docker (Qdrant, API, UI)

---

## 🧩 Key Components

### 1. Schema Generator Pipeline (`SchemaGenerator` & `SchemaBootstrapper`)
A standalone **6-Phase Checkpointed Knowledge Ingestion Pipeline** that bootstraps the AI's understanding of the database.

**Pipeline Phases:**
| Phase | Description |
|:---|:---|
| **1. Context Map** | Reads ERP metadata (Pages, PageAttribute, PageDetail) to understand structure |
| **2. Target Lists** | Identifies Views/Tables to process |
| **3. (Deferred)** | Reserved for future optimization |
| **4. Data Extraction** | Extracts metadata rows (Full Load, CDC-ready) |
| **5. Schema CDC** | Detects schema changes via `sys.objects.modify_date` |
| **6. Upload** | Syncs documents to Qdrant with checkpoint resiliency |

**Key Features:**
*   ✅ **Checkpoint Resiliency:** Automatic resume from last successful phase on crash.
*   ✅ **Schema CDC (Phase 5):** Detects schema changes via `sys.objects.modify_date` and only uploads changed Tables/Views (incremental updates).
*   ⚠️ **Data CDC:** Infrastructure built (`CdcChangeStrategy`), awaiting activation.
*   ⚠️ **Multi-Tenancy:** `tenant_id` support planned (Module 1 in roadmap).

**Components:**
*   `SchemaOrchestrator.cs` - Main orchestrator (350 lines)
*   `CdcChangeStrategy.cs` - SQL Server CDC integration (built, not wired)
*   `QdrantVectorDbService.cs` - Gemini embedding + Tier 1 rate limiting

---

### 2. 🆕 Query Condenser Layer 0.5 (`QueryCondenserService`)

A **pre-processing layer** that intelligently handles follow-up questions and determines the optimal SQL generation strategy.

**The Problem Solved:**
```text
User: "هات آخر 5 فواتير"                  → SQL: SELECT TOP 5 ... ORDER BY Date DESC
User: "رتبهم تنازلياً"                    → ???

Without Layer 0.5: LLM doesn't know what "them" refers to!
With Layer 0.5: Condenses to "هات آخر 5 فواتير ورتبهم تنازلياً" + ActionType=WrapPrevious
```

**ActionType System:**
| ActionType | When Used | SQL Strategy |
|:---|:---|:---|
| **New** | Completely new/unrelated question | Generate fresh SQL from scratch |
| **WrapPrevious** | Simple modification (sort, filter, limit) | Wrap previous SQL in subquery |
| **Regenerate** | Related context but needs new logic | Use context, generate new SQL |

**Key Files:**
| File | Purpose |
|:---|:---|
| `CondenserResult.cs` | Model + QueryActionType enum |
| `IQueryCondenserService.cs` | Interface definition |
| `QueryCondenserService.cs` | LLM-based implementation |
| `QueryCondenserServiceTests.cs` | 20+ unit tests |

**Flow Diagram:**
```text
┌─────────────────────────────────────────────────────────────────┐
│         Query Condenser Service (Layer 0.5)                     │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  INPUT:                                                         │
│  ├── currentQuestion: "رتبهم تنازلياً"                          │
│  ├── previousQuestion: "هات آخر 5 فواتير"                       │
│  └── previousSql: "SELECT TOP 5 ... ORDER BY Date DESC"        │
│                                                                 │
│  PROCESS:                                                       │
│  ├── 1. If no previous → Return ActionType.New (skip LLM)      │
│  ├── 2. Build smart prompt with WRAP/REGENERATE/NEW rules      │
│  ├── 3. Call LLM (Gemini)                                       │
│  └── 4. Parse response: "ACTION: WRAP\nQUESTION: ..."          │
│                                                                 │
│  OUTPUT: CondenserResult                                        │
│  ├── ActionType: WrapPrevious                                   │
│  └── CondensedQuestion: "هات آخر 5 فواتير ورتبهم تنازلياً"      │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

### 3. Dynamic AI Core (`DynamicAi.Application`)
The brain of the operation, containing the business logic.

*   **`QueryOrchestrator`:** Manages the 7-layer flow of a user request. 🆕 Now integrates Query Condenser (Layer 0.5) before caching.
*   **`SqlGenerationService`:** Constructs precise SQL queries based on user prompts and retrieved schema context. 🆕 Now accepts `ActionType` to handle WRAP vs REGENERATE modes.
*   **`QueryCacheService`:** *[MVP Feature]* Checks Qdrant for semantically similar past questions to return cached SQL instantly, bypassing the LLM for faster responses.
*   **`GeminiCostTracker`:** Centralized service that tracks token usage and calculates costs for Chat and Embedding models in real-time.

---

### 4. Dynamic AI UI (`DynamicAi.UI`)
A modern, responsive web interface built with **Angular (Latest)**.

*   **Chat Interface:** A ChatGPT-like experience for interacting with the data.
*   **Data Visualization:** Displays query results in clean, formatted tables.
*   **Feedback System:** Like/Dislike buttons with user comments for Golden Dataset collection.
*   **SQL Display:** Copy-to-clipboard functionality for viewing generated SQL.
*   🆕 **Dislike Context Rollback:** When user clicks "Dislike", the conversational context reverts to the last confirmed (non-disliked) state for seamless retry.

**🆕 Context Management (Two-Tier State):**
```typescript
// Current state (may be disliked)
lastQuestion: string | undefined
lastSql: string | undefined

// Rollback point (last confirmed good state)
confirmedQuestion: string | undefined
confirmedSql: string | undefined

// On successful response: confirmed = last
// On dislike: last = confirmed (rollback!)
```

---

### 5. Feedback & Golden Dataset Pipeline (`FeedbackService`)
A system for collecting user feedback to build a fine-tuning dataset.

| Feature | Description |
|:---|:---|
| **Like 👍** | Saves successful query to `TrainingExamples` table |
| **Dislike 👎** | 🆕 Saves failed query + Rolls back context for retry |
| **QueryLog** | Stores `SchemaContext`, `TenantId`, `CompanyId` for traceability |
| **TrainingExamples** | Full feedback record: NaturalQuery, GeneratedSql, SchemaContext, ErrorMessage |
| **Golden Dataset Export** | Verified examples can be exported to JSONL for fine-tuning |

---

## 🚀 Getting Started

### Prerequisites
*   [Docker Desktop](https://www.docker.com/products/docker-desktop) (**Optional**: Only if running Qdrant locally)
*   [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
*   [Node.js](https://nodejs.org/) (LTS version recommended)
*   SQL Server instance

### Installation & Setup

#### 1. Environment Setup (Windows)
If you encounter `npm` or `ng` errors, ensure your environment is configured correctly.

**Fix PowerShell Execution Policy:**
If `npm` fails to run, execute this in PowerShell as Administrator:
```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
```

**Install Angular CLI Globally (Optional but Recommended):**
```powershell
npm install -g @angular/cli
```

#### 2. Configure Secrets (Qdrant Cloud & Database)
Instead of editing `appsettings.json` directly, use **User Secrets** for security.

**Configure Qdrant Cloud:**
```powershell
dotnet user-secrets set "Qdrant:Host" "YOUR_QDRANT_CLOUD_URL" --project "DynamicAi/DynamicAi.Api.csproj"
dotnet user-secrets set "Qdrant:ApiKey" "YOUR_QDRANT_API_KEY" --project "DynamicAi/DynamicAi.Api.csproj"
```
*Note: The project is configured to use port `6334` (URI-based) for Qdrant Cloud to avoid HTTP/2 protocol errors.*

**Configure Database Connection:**
```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=YOUR_SERVER;Database=QualityControl;..." --project "DynamicAi/DynamicAi.Api.csproj"
```

#### 3. Run Schema Bootstrapper (First Time Only)
Initialize the vector knowledge base:
```powershell
cd SchemaBootstrapper
dotnet run
```

#### 4. Start the Backend API
**Critical:** You must trust the development SSL certificate to avoid connection errors in the browser.
```powershell
dotnet dev-certs https --trust
```
*Click "Yes" on the prompt.*

Run the API:
```powershell
dotnet run --project "DynamicAi/DynamicAi.Api.csproj" --launch-profile https
```
The API will be available at `https://localhost:7257`.

#### 5. Start the Frontend UI
Use `npm start` to run the project using the local Angular CLI version if you haven't installed it globally.
```powershell
cd DynamicAi.UI
npm install  # Install dependencies
npm start    # Runs 'ng serve'
```
Navigate to `http://localhost:4200` to use the application.

### 🐳 Docker Deployment (Optional)

The application supports Docker deployment with static file serving.

**CORS Origins Configured:**
*   `http://localhost:4200` - Angular dev server
*   `http://localhost:8080` - Docker container

**Static Files:** Angular build output in `wwwroot` is served automatically.

```powershell
# Build Angular for production
cd DynamicAi.UI
npm run build -- --output-path ../DynamicAi/wwwroot

# Run with Docker
docker-compose up
```

### 🔧 Troubleshooting
*   **"Unknown Error" in Chat:** This usually means the browser blocked the backend SSL certificate. Run `dotnet dev-certs https --trust` and restart your browser/use Incognito.
*   **Qdrant HTTP/2 Protocol Error:** Ensure your `Program.cs` is using the URI-based `QdrantClient` constructor with port `6334` for Cloud instances.
*   **`ng` not recognized:** Use `npm start` instead of `ng serve`.

---

## 🌟 Current Phase: Smart Query Condensing & Context Awareness

We are currently implementing **Query Condensing (Layer 0.5)** with **Smart ActionType** and **Dislike Context Rollback** features.

### ✅ Implemented Features

#### 1. 🆕 Query Condensing (Layer 0.5)
*   **Smart Follow-up Handling:** Understands "رتبهم" refers to previous results.
*   **ActionType System:** NEW / WRAP_PREVIOUS / REGENERATE modes for optimal SQL generation.
*   **Subquery Wrapping:** For WRAP mode, previous SQL is wrapped in subquery for modifications.
*   **SQL Server Compliance:** Handles ORDER BY in subqueries correctly (requires TOP/OFFSET).

#### 2. 🆕 Dislike Context Rollback  
*   **Two-Tier State:** Tracks both current and confirmed (rollback) context.
*   **Seamless Retry:** Clicking Dislike reverts to last successful state for clean retry.
*   **Frontend Integration:** Implemented in `app.ts` with `confirmedQuestion`/`confirmedSql`.

#### 3. Core Logic & Caching
*   **Semantic Caching:** Queries are hashed and compared using vector similarity (Qdrant).
*   **Context-Aware SQL Generation:** Retrieves relevant tables/views to reduce token usage.
*   **Clean Architecture:** Strict separation of concerns (Domain, Application, Infrastructure, API).

#### 4. Reliability & Safety
*   **Clarification Loop:** AI now asks "Clarification Needed" questions for ambiguous queries instead of hallucinating answers.
*   **Prompt Diet & Smart Context:**
    *   **Prompt Compression:** Reduced system prompt by **60%** (from ~10k to ~4k tokens) using specialized aliasing and syntax removal.
    *   **Explicit Gemini Caching:** Leverages semantic caching on the LLM side for identical prompts.
*   **SQL Validation Layer:** `SecurityVisitor` & `Rewriter` ensure generated SQL is safe and optimized. **(Verified by 82 Unit Tests ✅)**.
*   **Retry Policies:** Polly integration for resilient API calls.

#### 5. Observability & Cost Tracking
*   **Cost Tracker (`GeminiCostTracker`):** Real-time tracking of input/output tokens and cost calculation for every request.
*   **Structured Logging (`Serilog`):** Unified JSON-ready logs across all services for seamless monitoring.
*   **Request Metrics:** Tracks latency (`TotalDuration`) and Status Codes.

#### 6. Testing Strategy
*   **Unit Tests:** **82 tests** covering `SqlValidatorService`, `SqlRewriterService`, `SqlGenerationService`, `QueryCondenserService`, and `CondenserResult`.
*   **Integration Tests:** Verifies the full pipeline from `QueryOrchestrator` to `Gemini`.
*   **Mocking:** Uses `FakeItEasy` for reliable dependency simulation.

#### 7. Feedback & Training Data Collection
*   **Like/Dislike System:** Frontend buttons trigger `FeedbackController` endpoints.
*   **QueryLog Tracking:** Every query execution is logged with:
    *   `SchemaContext` - JSON of the page/view context used
    *   `TenantId` - Multi-tenant support
    *   `CompanyId` - Company identifier
    *   `ErrorMessage` - For failed executions
*   **TrainingExamples Table:** Stores verified (Like) and unverified (Dislike) examples.
*   **Golden Dataset Pipeline:** Verified examples → JSONL export → Fine-tuning ready.

### 🚧 Caching Logic Details
*   **Similarity Threshold:** Configurable (default `0.92`).
*   **TTL (Time To Live):** Cache entries expire after 7 days (default).
*   **Payload:** Stores `original_question`, `generated_sql`, and `tenant_id` to ensure data isolation.

---

## 📁 Project Structure

```text
DynamicAi/
├── DynamicAi.Application/          # Business Logic Layer
│   ├── Interfaces/
│   │   ├── IQueryOrchestrator.cs
│   │   ├── ISqlGenerationService.cs
│   │   └── IQueryCondenserService.cs    # 🆕 Layer 0.5
│   ├── Models/
│   │   ├── AskRequest.cs                # 🆕 previousQuestion/previousSql
│   │   └── CondenserResult.cs           # 🆕 ActionType enum + result
│   └── Services/
│       ├── QueryOrchestrator.cs         # 🆕 Integrates Layer 0.5
│       ├── SqlGenerationService.cs      # 🆕 WRAP/REGENERATE modes
│       └── QueryCondenserService.cs     # 🆕 LLM-based condensing
├── DynamicAi.Infrastructure/       # Data Access Layer
├── DynamicAi.Api/                  # API Layer (Controllers)
├── DynamicAi.UI/                   # Angular Frontend
│   └── src/app/
│       └── app.ts                       # 🆕 Context rollback logic
└── DynamicAI.Tests/                # Unit Tests
    ├── Services/
    │   ├── QueryCondenserServiceTests.cs  # 🆕 13 tests
    │   └── SqlGenerationServiceTests.cs
    └── Models/
        └── CondenserResultTests.cs        # 🆕 7 tests
```

---

## 🔮 Future Roadmap

Based on the **RAG Production Evaluation** (7 Layers & 6 Pillars), here is the prioritized roadmap to move from **MVP (Current)** to **Scale**.

### ✅ Recently Completed

| Feature | Status | Impact |
|:---|:---:|:---|
| **🆕 Query Condenser Layer 0.5** | ✅ Done | Smart follow-up handling + ActionType system |
| **🆕 Dislike Context Rollback** | ✅ Done | Seamless retry after dislike |
| **🆕 Subquery Wrapping (WRAP mode)** | ✅ Done | Efficient modifications on previous results |
| **Golden Dataset Pipeline** | ✅ Done | Like/Dislike → TrainingExamples → Fine-tuning ready |
| **QueryLog Tracking** | ✅ Done | SchemaContext, TenantId, CompanyId, ErrorMessage |
| **Docker Support** | ✅ Done | CORS for localhost:8080, static file serving |
| **Prompt Diet (ListName Fix)** | ✅ Done | Fixed column-to-table mapping in AI prompts |

### 🗺️ Development Phases

#### 1️⃣ Phase 2: Hardening (Months 2-3) 🔴 [High Priority]
*   **Goal:** Reliability & Safety (Fixing "Silent Failures" & "Hallucinations")
*   **Key Features:**
    *   **Reranking Layer:** Implement `IRerankingService` to re-score top-K documents (Reduces hallucinations by ~30%).
    *   **User Quotas:** Implement rate limiting and cost controls per tenant ("Cost Explosion").
    *   **Prompt Hardening:** Further refine prompt rules to handle edge cases in "Clarification Loop".

#### 2️⃣ Phase 3: Intelligence (Months 3-4) 🟡
*   **Goal:** Higher Recall & Context Quality
*   **Key Features:**
    *   **Hybrid Search:** Combine Vector Search + Keyword Search for exact matches (improves recall by ~20%).
    *   **Semantic Column Chunking:** Split large tables by semantic column groups in `SchemaReaderService.ChunkTableScript()`:
        *   **Financial Columns:** `ContractAmount`, `Tax`, `TotalWithTax` → One Document
        *   **Temporal Columns:** `ContractDate`, `ExpireDate`, `DeliveryDate` → One Document
        *   **Identity Columns:** `ContractId`, `ClientId`, `ContractNumber` → One Document
        *   Inspired by LangChain's `SQLDatabase` component for schema-aware splitting.
    *   **Confidence Scoring:** Expose AI confidence levels to the user.

#### 3️⃣ Phase 4: Advanced Observability (Months 4-5) 🟡
*   **Goal:** Deep Production Monitoring
*   **Key Features:**
    *   **Tracing:** OpenTelemetry integration for full request visibility.
    *   **Quality Metrics:** Automated evaluation of faithfulness and context recall.

#### 4️⃣ Phase 5: Scale & Fine-tuning (Months 5-6) ⚪
*   **Goal:** Massive Scale Support + AI Improvement
*   **Key Features:**
    *   **Fine-tuning with Golden Dataset:** Use collected TrainingExamples to fine-tune Gemini.
    *   **Embedding Fine-tuning:** Customize models for domain-specific ERP terminology.
    *   **Scaling Tests:** Validation for 100K+ schema objects.
    *   **Prompt Versioning:** Manage `Prompt Drift` with versioned registries.

### 📊 System Evaluation Summary

| Axis | Status | Score | Notes |
|:---|:---:|:---:|:---|
| **Query Condensing** | ✅✅✅ | 10/10 | 🆕 **Complete:** Layer 0.5 with ActionType system. |
| **Silent Failures** | ✅✅✅ | 10/10 | **Solved:** "Clarification Loop" + Validation Layer prevents almost all guessing. |
| **Prompt Engineering** | ✅✅✅ | 9/10 | **Optimized:** "Prompt Diet" + ListName fix. Strict Static Rules. |
| **Feedback System** | ✅✅✅ | 9/10 | **Complete:** Like/Dislike → TrainingExamples + Context Rollback. |
| **Vector DB** | ✅✅✅ | 9/10 | Qdrant with proper payload indexing is solid. |
| **Architecture** | ✅✅✅ | 9/10 | Clean Arch + Semantic Kernel + Explicit Caching Support. |
| **Observability** | ✅✅ | 8/10 | Cost tracking & Structured logging + QueryLog tracking. |
| **Cost Control** | ✅✅ | 8/10 | **Improved:** 60% less input tokens per request + Caching. |
| **Embedding** | ✅✅ | 8/10 | Gemini models are performing well. |
| **Containerization** | ✅✅ | 8/10 | Docker support with CORS and static files. |
| **Doc Parsing** | ✅ | 6/10 | SMO-based SQL parsing is accurate but limited to SQL. |
| **RAG Scaling** | ⚠️ | 6/10 | Needs validation for larger datasets. |
| **Latency** | ⚠️ | 6/10 | High (20s+) but mitigated by Caching. Streaming deemed unnecessary for SQL. |
| **Chunking** | ⚠️ | 5/10 | Needs "Semantic Column Chunking" (Phase 3) for large tables. |
| **Hybrid Search** | ❌ | 2/10 | **Missing:** Pure vector search misses exact keywords. |
| **Reranking** | ❌ | 0/10 | **Critical:** Major cause of hallucinations (retrieving wrong tables). |

---

## 📝 Changelog

### v1.2.0 (2026-01-07) - Query Condensing Release 🆕

**New Features:**
- ✅ **Query Condenser (Layer 0.5):** Smart follow-up question handling with LLM-based ActionType determination.
- ✅ **ActionType System:** NEW / WRAP_PREVIOUS / REGENERATE modes for optimal SQL generation strategy.
- ✅ **Subquery Wrapping:** WRAP mode wraps previous SQL in subquery for sort/filter/limit operations.
- ✅ **Dislike Context Rollback:** Clicking Dislike reverts conversational context to last confirmed state.
- ✅ **SQL Server Subquery Fix:** Correct handling of ORDER BY in subqueries (TOP + ORDER BY kept together).

**New Files:**
- `CondenserResult.cs` - Model with QueryActionType enum
- `IQueryCondenserService.cs` - Interface for Layer 0.5
- `QueryCondenserService.cs` - LLM-based implementation
- `QueryCondenserServiceTests.cs` - 13 unit tests
- `CondenserResultTests.cs` - 7 unit tests

**Modified Files:**
- `AskRequest.cs` - Added previousQuestion/previousSql
- `QueryOrchestrator.cs` - Integrates Layer 0.5 before caching
- `SqlGenerationService.cs` - WRAP/REGENERATE prompt modes
- `app.ts` - Two-tier context management (confirmed/last)

**Test Coverage:**
- Total tests: 82 (up from 62)

---

## 📄 License

This project is proprietary software developed by **EGY Dynamic**.
