# DynamicAI 🧠

> **The Enterprise-Grade Generative BI Platform for SQL Server**

**Dynamic AI** is a production-ready, full-stack AI solution that transforms natural language into optimized, secure SQL queries. Built on a robust **Clean Architecture**, it bridges the gap between raw data and business users using **Google Gemini** (LLM) and **Qdrant** (Vector Search).

Unlike simple text-to-SQL wrappers, Dynamic AI is an **Intelligent System** featuring a **Self-Evolving Hybrid Router**, **Triple-Layer Caching**, **Enterprise Cost Governance**, and a **Context-Aware Data Loop** that learns from every interaction.

---

## 🏗️ Architecture Overview

The system implements an **8-Layer Intelligence Pipeline** designed for high throughput, zero hallucinations, and cost efficiency.

```text
     User (Business / Analyst)
      ↕
[DynamicAi UI (Angular 17+)]  ◄── 🆕 Stop Button (Cancel mid-pipeline)
      ↕
[DynamicAi API (.NET 8 Clean Arch)] ───────┬────────────────────────────┐
      │                                    │                            │
      │  🆕 STARTUP WARMUP LAYER           │                            │
      │  (DbSchemaCacheWarmer +            │                            │
      │   IntentIndexWarmer +              │                            │
      │   QueryCacheWarmer)                │                            │
      │                                    │                            │
                  ┌────────────────────────▼─────────────────────────┐  │
                  │   💎 FINANCIAL & TELEMETRY OBSERVABILITY LAYER   │  │
                  │      (LlmUsageTracker + Daily Budget Limiter)    │  │
                  └────────────────────────┬─────────────────────────┘  │
                                           │                            │
                  ┌────────────────────────▼─────────────────────────┐  │
                  │              Query Orchestrator                  │  │
                  │  (Central Coordinator + 🆕 Shared Embedding)     │  │
                  └────────────────────────┬─────────────────────────┘  │
                                           │                            │
                  ┌────────────────────────▼─────────────────────────┐  │
                  │   Layer 0: Intelligent Hybrid Router             │  │
                  │ (Vector Search + LLM Classification + Fallback)  │  │
                  └───────┬────────────────┬────────────────┬────────┘  │
                          │                │                │           │
    ┌─────────────────────┘                │                └───────────┼──────────┐
    │ 'Chat' Intent                        │ 'SQL' Intent               │ 'Predict'Intent
    ▼                                      ▼                            ▼
┌──────────────────┐           ┌──────────────────────────┐    ┌────────────────────┐
│ ChatHandler      │           │      SQL Handler         │    │   PredictHandler   │
│(Lightweight LLM) │           │   (The Core Engine)      │    │ (Data extrapolation│
└──────────────────┘           └───────────┬──────────────┘    │  on Context)       │
                                           │                   └──────────┬─────────┘
                               ┌───────────▼───────────┐                  │
                               │ Layer 0.5: Condenser  │                  │
                               │(Smart Action Decisions)│                 │
                               └───────────┬───────────┘                  │
                                           │                              │
                               ┌───────────▼───────────┐                  │
                               │ Layer 1: Intent Cache │                  │
                               │ (Semantic / <50ms)    │                  │
                               └───────────┬───────────┘                  │
                                           │                              │
                               ┌───────────▼───────────┐                  │
                               │ Layer 2: Context Cache │                 │
                               │ (RAG + 🆕 Smart       │                 │
                               │  Re-Ranking / <10ms)  │                  │
                               └───────────┬───────────┘                  │
                                           │                              │
                               ┌───────────▼───────────┐                  │
                               │ Layer 3: Response Cache│                 │
                               │ (Gemini Smart Key)    │                  │
                               └───────────┬───────────┘                  │
                                           │                              │
                               ┌───────────▼───────────┐                  │
                               │🆕Layer 3.5: Sanitizer │                  │
                               │ (Column Auto-Fixer)   │                  │
                               └───────────┬───────────┘                  │
                                           │                              │
                               ┌───────────▼───────────┐                  │
                               │ Layer 4: Validation   │                  │
                               │ (ScriptDom Security)  │                  │
                               └───────────┬───────────┘                  │
                                           │                              │
                               ┌───────────▼───────────┐                  │
                               │ Layer 5: Execution    │◄─────────────────┘
                               │ (SQL Server Dapper)   │  (Multi-Step Fetches)
                               └───────────┬───────────┘
                                           ↕
                                   [(SQL Database)]
```

### 🚀 Key Technical Pillars

1.  **Full-Stack & Production Ready**
    *   **Frontend:** Modern Angular 17+ with Material Design, Markdown rendering, and Syntax Highlighting.
    *   **Backend:** .NET 8 Web API following **Clean Architecture** (Domain, Application, Infrastructure, Api).
    *   **Database:** SQL Server (Target) + Qdrant (Vector Intelligence).
    *   **Observability:** Real-time dashboards, structured logging, and cost tracking.

2.  **Triple-Layer Caching Architecture (The "Speed" Layer)**
    *   **Semantic Cache:** Instantly returns semantically identical questions (e.g., "Show sales" == "Revenue").
    *   **RAG Context Cache:** Eliminates redundant Vector DB lookups for schema metadata.
    *   **LLM Response Cache:** Leverages Gemini's Context Caching to bypass generation for known schema states.

3.  **Self-Evolving Hybrid Router (The "Brain" Layer)**
    *   **Zero-Shot Classification:** Uses Vector Search to route queries to the correct handler (`Chat`, `Sql`, `Predict`).
    *   **Auto-Seeding:** Automatically learns from new questions to improve future routing accuracy.

4.  **Defense-in-Depth Security**
    *   **Validation Layer:** Uses Microsoft's `ScriptDom` to parse and efficiently block any DML (INSERT/UPDATE/DELETE) or DDL (DROP/ALTER) commands before execution.
    *   **SQL Sanitization Layer:** `SanitizationVisitor` + `SqlSanitizerService` surgically removes hallucinated columns from LLM-generated SQL using AST analysis.
    *   **Read-Only Context:** The LLM is never given direct write access credentials.

5.  **Smart Re-Ranking (Hybrid RAG)**
    *   **Semantic + Keyword:** Combines Qdrant vector search with Arabic-aware keyword tiebreaking for 100% page accuracy.
    *   **Score Delta Guard:** Only re-ranks when candidates are within 0.05 cosine distance.

6.  **Financial Governance & ROI Tracking**
    *   **Token Accuracy:** Full extraction of actual tokens from Gemini API.
    *   **Rate Limiting:** Circuit breakers at the Tenant level to prevent bill shock.

### 🛠️ Technology Stack

*   **Backend:** .NET 8 (C#)
*   **Frontend:** Angular 17+ (TypeScript, SCSS)
*   **Vector Database:** Qdrant (for schema embeddings & query caching)
*   **LLM:** Google Gemini (via Semantic Kernel)
*   **Database:** SQL Server + EF Core (QueryLogs, TrainingExamples)
*   **Containerization:** Docker (Qdrant, API, UI)

---

## 🛡️ Multi-Layered Defense System (Anti-Hallucination)

> **"You can't 'fix' hallucinations; you must engineer around them."**

Dynamic AI treats the LLM as a probabilistic component wrapped in a deterministic "Iron Dome" defense system. We employ a **6-Layer Defense-in-Depth Strategy** to ensure enterprise-grade reliability:

### 1. Context Grounding (The RAG Layer)
*   **Goal:** Restrict the model's "world view" to reality.
*   **Mechanism:** Instead of dumping the entire schema, `QdrantRetrievalService` performs a multi-step semantic search with **Smart Re-Ranking** (keyword tiebreaker for Arabic-aware accuracy).
*   **Result:** The LLM *cannot* hallucinate tables that don't exist because it never sees them. It only sees the specific Pages/Attributes relevant to the user's question.

### 2. Strict Schema Mapping (The Constraint Layer)
*   **Goal:** Enforce a "Contract of Reality".
*   **Mechanism:** `SqlGenerationService` uses a static, cached system prompt with strict rules:
    *   **Rule 3:** "ALLOWED TABLES ONLY... Any other table/view=FORBIDDEN."
    *   **Rule 4:** "Resolve columns ONLY via ListSetups... NEVER GUESS."
*   **Outcome:** The model is legally bound by the prompt to output `CLARIFICATION_NEEDED` rather than guessing.

### 3. Post-Generation Validation (The Guard Layer)
*   **Goal:** Trust, but verify with a compiler.
*   **Mechanism:** Before execution, `SqlValidatorService` parses the raw SQL using Microsoft's **ScriptDom** (AST Parser).
    *   **SecurityVisitor:** A custom C# visitor traverses the syntax tree to strictly block `DELETE`, `UPDATE`, `DROP`, and other DDL/DML vectors.
*   **Benefit:** Catches malicious injections or hallucinated syntax *before* they touch the database.

### 4. 🆕 SQL Sanitization (The Auto-Fixer Layer)
*   **Goal:** Recover from column-level hallucinations instead of failing.
*   **Mechanism:** `SqlSanitizerService` + `SanitizationVisitor` use AST analysis to:
    *   Resolve aliases (`T1 → PaymentReciveView`) via `DbSchemaProvider`.
    *   Detect columns that don't exist in the actual database schema.
    *   **Surgically prune** invalid columns from `SELECT`, `WHERE`, `ORDER BY`, and `GROUP BY` — then rebuild clean SQL.
*   **Benefit:** Instead of a hard failure, the query succeeds with valid columns only.

### 5. Safety & Feedback Loop (The Execution Layer)
*   **Goal:** Fail gracefully and learn.
*   **Mechanism:**
    *   **Sandboxed Execution:** `ISandboxedSqlExecutor` runs the query with read-only permissions.
    *   **Trap & Log:** Any failure is trapped by `QueryOrchestrator` and logged to `QueryLog` with `WasSuccessful=false`.
*   **Self-Correction:** These logs fuel the **Golden Dataset**, turning every failure into a future training example.

### 6. 🆕 Smart Re-Ranking (The Precision Layer)
*   **Goal:** Ensure the RAG layer picks the *right* page, not just the *closest* one.
*   **Mechanism:** When Top-3 page candidates are within 0.05 cosine distance, a keyword tiebreaker (`PageDisplay` Arabic name) promotes the most relevant result.
*   **Benefit:** Eliminates "similar but wrong page" errors in Arabic ERP context.

---

## 💎 Enterprise LLM Cost Governance (Financial Guardrails)

> **"AI is only as good as its ROI. We don't just track costs; we control them."**

In v1.6.0, Dynamic AI introduced a sophisticated **LLM Financial Governance System**. This is a mission-critical layer for any production-grade AI system, transforming unpredictable API bills into a predictable, manageable business expense.

### 🛡️ The Financial "Iron Dome" Architecture

We've implemented a **Non-Bypassable Cost Tracking & Limiting Layer** integrated across 16 critical AI call points:

1.  **Precision Unit Tracking (Token Extraction)**:
    *   **Mechanism**: Uses the official **Google.GenAI / Semantic Kernel SDKs** to extract *actual* token usage from every raw response.
    *   **Granularity**: Tracks Input Tokens, Output Tokens, and Total Tokens with 100% precision (no estimations).

2.  **Real-time USD Financial Mapping**:
    *   **Architecture**: A high-performance usage service (`LlmUsageTracker`) maps tokens to current model pricing (e.g., Gemini 2.0 Flash/Pro) with **8-decimal USD precision**.
    *   **Impact**: Executives see exact financial impact per query, per team, and per dashboard.

3.  **Active Circuit Breakers (Daily Budgeting)**:
    *   **Dynamic Rate Limiting**: Every Tenant has a **Daily USD Budget**. Once reached, the system triggers an automatic **Circuit Breaker**, gracefully denying new AI requests to prevent "Bill Shock."
    *   **Self-Healing**: Limits auto-reset at 00:00 UTC, ensuring continuous operations without manual intervention.

4.  **Glassmorphism Financial Dashboard**:
    *   A premium, real-time UI that visualizes:
        *   **💰 Total Burn**: Daily spending against quotas.
        *   **📈 Efficiency Ratio**: Cost vs. Value (Intent distribution).
        *   **📊 Multi-Tenant Visibility**: Breakdown of usage by Company/User.

### 💰 Why This Matters:
*   **Prevent "Toxic Queries"**: Automatically blocks infinite character loops or recursive hallucinations from draining tokens.
*   **ROI Validation**: Quantify the cost of a "Chat" versus a "SQL Generation" to optimize resource allocation.
*   **Scale with Confidence**: Deploy to 100+ clients knowing that no single user can consume the entire organization's AI budget.

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

## 🧠 The Self-Evolving Hybrid Router

The core of Dynamic AI is its **Intelligent Intent Orchestrator**, which evolves from zero data to highly accurate vector-based routing through 4 strategic phases:

### Phase 0: "The Cold Start Solution"
*   **Problem:** LLMs don't know the specific patterns of your database.
*   **Solution:** **Incremental Intent Seeding**. On startup, the system checks for missing intent examples and seeds them automatically using `IIntentExampleManagementService.SeedMissingExamplesAsync()`.
*   **Impact:** Ensures the system is functional from the very first query.

### Phase 1: "The Brain" (IntentClassificationService)
*   **Mechanism:** A **Semantic Router** that uses vector embeddings (Qdrant) to match questions against known intents.
*   **Self-Learning Loop:** If no match is found (Low Confidence < 0.75), it falls back to the LLM to classify and **automatically seeds** the result back to Qdrant for future instant matching.

### Phase 2: "The Safety Net" (Admin Review & Caching)
*   **Low Confidence Handling:** Queries that fail the confidence threshold are saved to `intent_examples` with `Status=Pending` for admin review.
*   **Cache Management:** Admins can clear specific cache entries or review intents via dedicated endpoints.

### Phase 3: "The Handlers" (Specialized Execution)
1.  **`SqlIntentHandler`**: For `DataRetrieval` and `Analysis`. Manages the full SQL pipeline: Condensing → RAG → Generation → Validation.
2.  **`ChatIntentHandler`**: For casual greetings and general questions (No DB access).
3.  **`PredictIntentHandler`**: For future insights. Uses **Conversation Context** to predict trends based on previously fetched data.
4.  **`PredictionWithDataFetch`**: A **Multi-Step Pipeline** that extracts a data-query first, executes it, and then feeds the result into the prediction engine.

### 💹 Financial Governance Dashboard (Preview)

```text
┌────────────────────────────────────────────────────────────────────────┐
│ 💎 LLM FINANCIAL GOVERNANCE SYSTEM                                     │
├────────────────────────────────────────────────────────────────────────┤
│  [ DAILY BUDGET: $5.00 ]                      [ BURN RATE: $0.12/hr ]  │
│  ████████░░░░░░░░░░░░░  $1.25 used (25%)                               │
├────────────────────────────────────────┬───────────────────────────────┤
│  BY INTENT:                            │  BY MODEL:                    │
│  - DATA RETRIEVAL: $0.85 (68%)  [■■■]  │  - Gemini 2.0 Flash: $0.95    │
│  - ANALYSIS:       $0.30 (24%)  [■]    │  - Gemini 2.0 Pro:   $0.30    │
│  - CHAT:           $0.10 (08%)  [ ]    │                               │
└────────────────────────────────────────┴───────────────────────────────┘
```

---

## 🔄 Context-Aware Data Loop ("Memory Task")

Dynamic AI doesn't just answer isolated questions; it remembers the conversation context using a specialized loop:

*   **`ConversationContextService`**: Manages the state using `IMemoryCache` and persistent logs.
*   **`ConversationTurn` System**: Stores each turn with:
    *   **Question**: The original user query.
    *   **SQL Executed**: The query that ran.
    *   **Result Sample JSON**: A smart summary of the result set (to stay within token limits).
*   **Context Injection**: Injected at "Layer -1" of the Orchestrator, allowing handlers (like Predict) to "see" what was just discussed or fetched.

---

### 4. Dynamic AI Core (`DynamicAi.Application`)
The brain of the operation, containing the business logic.

*   **`QueryOrchestrator`**: Coordinates the entire pipeline. Delegates to Handlers based on detected Intent. Uses **Shared Embedding** (v1.7.0).
*   **`IntentClassificationService`**: The "Router" that determines if the user wants SQL, Chat, or Prediction.
*   **`PredictIntentHandler`**: Uses recently fetched data results to provide intelligent extrapolations (e.g., "Will this continue next month?").
*   **`ConversationContextService`**: Enables the "Data Loop" by storing and retrieving sampled query results for subsequent steps.
*   **`SqlGenerationService`**: Constructs precise SQL Queries. Accepts `ActionType` (NEW/WRAP/REGENERATE) from the Condenser.
*   **`QueryCondenserService`**: Handles Layer 0.5 (Follow-up logic).
*   **`SqlValidatorService`**: Security layer using **ScriptDom** to prevent DML/DDL operations.
*   **`SqlSanitizerService`**: 🆕 Post-generation auto-fixer that surgically removes hallucinated columns via AST analysis.
*   **`SanitizationVisitor`**: 🆕 Dual-purpose ScriptDom visitor: builds alias maps + detects invalid columns.
*   **`DbSchemaProvider`**: 🆕 Fetches Tables + Columns from `sys.objects` (10min cache + SemaphoreSlim).
*   **`QdrantRetrievalService`**: RAG retrieval with 🆕 **Smart Re-Ranking** (Arabic keyword tiebreaker).
*   **`GeminiCostTracker`**: Precise USD cost calculation (8 decimal places) per request.
*   **`IntentExampleManagementService`**: Handles index creation and **Incremental Seeding** of missing intent examples.

### 5. Dynamic AI UI (`DynamicAi.UI`)
A modern, responsive web interface built with **Angular (Latest)**.

*   **Chat Interface:** A ChatGPT-like experience for interacting with the data.
*   **🆕 Stop Button:** Full end-to-end cancellation — frontend unsubscribes the HTTP Observable (aborting XHR), backend propagates `CancellationToken` through the entire pipeline (Embedding → Intent → Condenser → SQL Gen → Execution), halting all async operations. Controller catches `OperationCanceledException` and returns HTTP 499. Follows the ChatGPT UX pattern (Jakob's Law).
*   **Data Visualization:** Displays query results in clean, formatted tables.
*   **Dashboard:** Real-time glassmorphism dashboard tracking:
    *   **Performance:** P95/P99 Latency, Error Rates.
    *   **Intelligence:** Intent Distribution (SQL vs. Chat vs. Predict).
    *   **Cost & Governance (M6/M7):** Glassmorphism dashboard tracking:
        *   **Daily Quotas**: Progress bars showing usage vs. $USD Limit.
        *   **Token Breakdown**: Precise Input/Output token split.
        *   **Cost Distribution**: Tracking expenditure across different Intent types.
    *   **CSAT:** User Satisfaction Score derived from Like/Dislike feedback.
*   **Feedback System:** Like/Dislike buttons with user comments for Golden Dataset collection.
*   **SQL Display:** Copy-to-clipboard functionality for viewing generated SQL.

---

### 6. User-Centric Observability Backend
A foundational layer enabling precise tracking of system performance and user satisfaction.

*   **`MetricsService`:** Aggregates `QueryLog` data to provide real-time insights (CSAT, Speed, Cost).
*   **`GeminiTokenCounter`:** Uses official **Google.GenAI SDK** for accurate token counting.
*   **`FeedbackService`:** Closes the loop by updating logs with user sentiment (Like/Dislike), directly influencing CSAT scores.
*   **`MetricsController`:** Exposes Tier 1-4 metrics endpoints (`/performance`, `/intent-distribution`, `/cost-breakdown`, `/user-experience`).

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

## ⚡ Performance & Token Efficiency (v1.7.0 — Latest)

> **"We don't just cache results; we cache intelligence. And we pre-warm everything."**

Across v1.5.0 → v1.7.0, we introduced a **Triple-Layer Caching Architecture** + **3x Background Warmup Services** + **Shared Embedding** that transformed the system from 25s cold-start to sub-2s warm queries.

### 📊 The Impact (Evolution: v1.5.0 → v1.7.0)

| Metric | v1.4.0 (Legacy) | v1.5.0 (Cache) | v1.7.0 (Warmup) | Improvement |
|:---|:---:|:---:|:---:|:---:|
| **Cold Start (1st Query)** | ~25s | ~10s | **~3.3s** | **87% Faster** 🚀 |
| **Warm Query (Cache HIT)** | ~5.2s | ~1.9s | **~1.5s** | **72% Faster** 🚀 |
| **Full Pipeline (Cache MISS)** | ~12.5s | ~10.5s | **~7.0s** | **33% Faster** 📉 |
| **Token Usage / Query** | ~4,500 | ~150 | **~150** | **96% Reduction** 📉 |
| **Cost Per 1k Queries** | ~$15.00 | ~$0.80 | **~$0.80** | **94% Savings** 💰 |
| **Schema Context Impact** | Full Re-fetch | Zero-Fetch | **Pre-Warmed** | Zero Cold Cost |

### 🧠 The Triple-Layer Caching Architecture

We don't rely on a single cache. We use a **Defense-in-Depth** strategy for performance:

1.  **Layer 1: Semantic Intent Cache (The Router)**
    *   **What it does:** Instantly recognizes repeated semantic questions (e.g., "Show sales" = "Revenue").
    *   **Tech:** Qdrant Vector Search.
    *   **Speed:** <50ms.

2.  **Layer 2: RAG Context Cache (The Knowledge)**
    *   **What it does:** Caches the *retrieved schema metadata* so we don't need to re-query the Vector DB for the same page context.
    *   **Tech:** In-Memory + Smart Invalidation.
    *   **Speed:** <10ms.

3.  **Layer 3: Generative Response Cache (The LLM)**
    *   **What it does:** Caches the *generated SQL* linked to a specific Schema State.
    *   **Tech:** Gemini Context Caching with **Smart Keys**.
    *   **Speed:** ~1.5s (vs 10s+ for full generation).

### 🛠️ The "Smart Cache Key" Mechanism

Dynamic AI uses a **Context-Aware Smart Key** that ensures you never get stale data, even when caching deeply.

**The Formula:**
```csharp
var cacheKey = $"{PageId}_{SchemaHash}_{TermCount}_v3";
```

1.  **`PageId`**: Scopes the cache to the specific dashboard the user is on.
2.  **`SchemaHash`**: A cryptographic hash of all *visible* data structures. If a column changes or a permission is revoked, the hash changes, invalidating the cache instantly.
3.  **`TermCount`**: Ensures that if new business terminology is added, the LLM re-generates the SQL to understand it.

### 🛡️ Engineering Excellence: The "Context Decoupler"

We performed a major architectural refactor to solve LLM hallucination:
*   **The Challenge:** Relying on single-source retrieval often led to "stale" metadata in high-throughput scenarios.
*   **The Solution:** We **decoupled** the retrieval of Metadata (Attributes) from the assembly of the Schema Context.
*   **The Result:** The LLM receives a **100% accurate, up-to-the-millisecond** map of the underlying database structure, ensuring it uses the mathematically correct table names without guessing.

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

---

## 📡 API Reference (Notable Endpoints)

### 🧩 Intent & Data Seeding
*   `POST /api/dataseeder/seed`: Full database seeding.
*   `POST /api/DataSeeder/seed-intents`: **Incremental Seeding**. Injects missing intent examples without duplicates.

### 🧠 Intent Review (The Safety Net)
*   `POST /api/intent-review/clear-by-question`: Remove specific intent examples.

### ⚡ Query Cache
*   `POST /api/query-cache/clear-by-question`: Clear specific cached SQL result by question.
*   `DELETE /api/query-cache/{id}`: Delete specific cache entry.


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

## 🌟 Current Phase: Performance & UX Excellence (v1.7.0)

We have achieved **87% cold-start improvement** through 3x Background Warmup Services, Shared Embedding, and RAG Context Caching — transforming Dynamic AI into a truly instant-response intelligence platform.

### ✅ Implemented Features (v1.7.0, v1.6.0, v1.5.0 & v1.4.0)

#### 1. 🚀 Performance Revolution: Zero Cold-Start Architecture (New in v1.7.0)
*   **3x Background Warmup Services**: `DbSchemaCacheWarmer`, `IntentIndexWarmer`, `QueryCacheWarmer` — all indexes and schemas pre-loaded at startup.
*   **Shared Embedding Optimization**: Single embedding computation reused across Intent Classification + RAG Retrieval (saves ~800-1300ms/query).
*   **RAG Context Cache (240min TTL)**: Cached page metadata eliminates repeat Vector DB lookups entirely.
*   **Result**: Cold start dropped from **25s → 3.3s** (87% faster), warm queries at **~1.5s**.

#### 2. ⛔ Stop Button — Full Pipeline Cancellation (New in v1.7.0)
*   **UX Pattern**: Replaces Send button during loading (ChatGPT-style, Jakob's Law).
*   **Frontend**: Unsubscribes the active HTTP Observable → aborts the XHR request.
*   **Backend**: `HttpContext.RequestAborted` propagated as `CancellationToken` through **every async call** in the pipeline:
    *   Embedding (`GenerateAsync`) → Intent Classification → Condenser → SQL Generation → Validation → Execution.
*   **Controller**: Catches `OperationCanceledException` → logs `⛔` → returns **HTTP 499** (Client Closed Request).
*   **Feedback**: Displays `"⛔ تم إيقاف التوليد بواسطة المستخدم"` — user stays in control.

#### 3. 💎 Financial Governance & Cost Excellence (v1.6.0)
*   **Precision Token Extraction**: Integrated directly with Google GenAI SDK for 100% accurate token counting.
*   **USD Mapping Engine**: Real-time conversion of tokens to actual financial cost ($USD).
*   **Daily Budget Circuit Breaker**: Automatic stopping of AI requests once a tenant's daily limit is reached.
*   **Financial Dashboard**: High-fidelity glassmorphism UI for tracking AI spend and ROI.

#### 4. ⚡ The Triple-Layer Caching Engine (v1.5.0 Breakthrough)
*   **Layer 1: Semantic Intent Cache**: Instant recognition of repeated questions via Qdrant (<50ms).
*   **Layer 2: RAG Context Cache**: In-memory metadata storage to bypass Vector DB for known pages.
*   **Layer 3: Gemini Smart Cache**: Context-aware caching of complex SQL generation logic.

#### 5. 🛡️ Engineering Excellence: The Context Decoupler (v1.5.0)
*   **Problem**: High-concurrency "stale" metadata risk.
*   **Solution**: Decoupled schema fetch from context assembly.
*   **Result**: 100% schema accuracy and zero table-name hallucinations.

#### 6. 🧠 The Hybrid Router (v1.4.0 Baseline)
*   **Phase 0-3 Evolution**: From cold-start seeding to vector-based classification with LLM fallback.
*   **Specialized Handlers**: Decoupled execution paths for `Sql`, `Chat`, and `Predict` intents.

#### 7. 🔄 Context-Aware Data Loop & Memory
*   **Turn-Based Memory**: Persistent storage of conversation history with smart result sampling.
*   **Cross-Handler Intelligence**: Predict Handler leverages data previously fetched by the SQL Handler.

#### 8. 🤖 Self-Healing Training Loop (v1.6.0 Enhancement)
*   **Auto-Capture M13**: Automated background task that saves successful queries to the Training Dataset.
*   **Golden Dataset Management**: UI for verifying, rejecting, and exporting (`.jsonl`) training examples for fine-tuning.
*   **Duplicate Prevention**: Backend validation ensures every query is only captured once.

#### 9. 📈 Enterprise Data Actions (v1.6.0)
*   **Advanced Export**: One-click Excel/CSV export of AI-generated result sets.
*   **Smart Print Styling**: Specialized CSS for clean, professional PDF/Paper reporting.
*   **SQL View Modal**: Instant access to generated code for transparency and debugging.

#### 10. 🔬 SQL Sanitization Layer (New in v1.7.0)
*   **Auto-Fixer**: `SqlSanitizerService` + `SanitizationVisitor` surgically prune hallucinated columns using AST analysis.
*   **Schema Provider**: `DbSchemaProvider` fetches all Tables + Columns from `sys.objects` (cached 10min).
*   **Alias Resolution**: Maps `T1 → Table` aliases, then validates each column against real schema.
*   **Surgical Removal**: Removes invalid columns from `SELECT`, `WHERE`, `ORDER BY`, `GROUP BY` — rebuilds clean SQL.

#### 11. 🎯 Smart Re-Ranking (New in v1.7.0)
*   **Hybrid Page Discovery**: Combines Qdrant semantic search (Top-3) with Arabic keyword tiebreaking.
*   **Score Delta Guard**: Only activates re-ranking when candidates are within 0.05 cosine distance.
*   **Filed Priority**: `PageDisplay` (Arabic) → `PageName` → `EnName` for keyword matching.

#### 12. 🛠️ Management & Reliability
*   **Incremental Seeding**: Safety injecting new intents without overwriting manual tweaks.
*   **SQL Validation**: ScriptDom-based AST parsing to block DML/DDL operations.
*   **Clarification Loop**: System asks for more info rather than guessing ambiguous queries.

---

## 🔬 SQL Sanitization Layer 

> **"The LLM can hallucinate columns. We surgically remove them before they break execution."**

A post-generation **Auto-Fixer** layer that validates LLM-generated SQL against the actual database schema. Instead of rejecting invalid SQL entirely, it **surgically prunes** hallucinated columns while preserving the valid query structure.

### Architecture

```text
[LLM Generated SQL]
        ↓
┌──────────────────────────────────────────┐
│  Layer 3.5: SQL SANITIZER               │
│                                         │
│  1. DbSchemaProvider                    │
│     → Fetches Tables + Columns          │
│     → INFORMATION_SCHEMA + sys.objects  │
│     → 10min cache + SemaphoreSlim       │
│                                         │
│  2. SanitizationVisitor (ScriptDom)     │
│     → Builds Alias Map (T1 → Table)     │
│     → Identifies Invalid Columns        │
│     → Dual-Purpose AST Visitor          │
│                                         │
│  3. SqlSanitizerService                 │
│     → Removes Invalid SELECT elements   │
│     → Cleans ORDER BY / GROUP BY        │
│     → Prunes WHERE conditions           │
│     → Rebuilds clean SQL from AST       │
└──────────────────────────────────────────┘
        ↓
[Sanitized SQL → Validator → Executor]
```

### Key Components

| Component | File | Role |
|:---|:---|:---|
| **`IDbSchemaProvider`** | `Interfaces/IDbSchemaProvider.cs` | Interface: `GetSchemaMapAsync()` returns `Dictionary<Table, HashSet<Column>>` |
| **`DbSchemaProvider`** | `SqlEngine/DbSchemaProvider.cs` | Queries `sys.objects` + `sys.columns`, cached 10min, SemaphoreSlim stampede protection |
| **`ISqlSanitizerService`** | `Interfaces/ISqlSanitizerService.cs` | Interface: `SanitizeAsync(sql)` returns sanitized SQL |
| **`SanitizationVisitor`** | `SqlEngine/SanitizationVisitor.cs` | Dual-purpose ScriptDom visitor: alias mapping + invalid column detection |
| **`SqlSanitizerService`** | `SqlEngine/SqlSanitizerService.cs` | Orchestrates the sanitization pipeline with nested AST visitors |

### How It Works

1.  **Schema Fetch**: `DbSchemaProvider` queries `sys.objects` + `sys.columns` (cached 10min with `SemaphoreSlim` to prevent stampede).
2.  **AST Parse**: SQL is parsed into an AST using `TSql160Parser` (ScriptDom).
3.  **Alias Resolution**: `SanitizationVisitor` maps aliases to tables (e.g., `T1 → PaymentReciveView`, `u → Users`).
4.  **Invalid Column Detection**: Columns not found in the schema map are flagged.
5.  **Surgical Removal**: `SelectElementRemover`, `ClauseElementRemover`, and `WhereClauseRemover` prune bad columns from `SELECT`, `ORDER BY`, `GROUP BY`, and `WHERE` clauses.
6.  **Clean Rebuild**: The modified AST is regenerated into clean SQL.

---

## 🎯 Smart Re-Ranking (Page Discovery Enhancement)

> **"Semantic similarity isn't always enough. Keywords break the tie."**

The RAG retrieval layer uses a **Hybrid Re-Ranking** strategy that combines Qdrant's semantic vector search with keyword-based tiebreaking for Arabic-aware page matching.

### Mechanism

1.  **Semantic Search**: Qdrant returns Top-3 page candidates sorted by cosine similarity.
2.  **Score Delta Check**: If candidates are within `0.05` score delta, the re-ranker activates.
3.  **Keyword Tiebreaker**: Extracts Arabic keywords from the user question (length > 2 chars) and counts hits against:
    *   `PageDisplay` (Arabic name — **primary** match field)
    *   `PageName` (English name)
    *   `EnName` (English alternate)
4.  **Promotion**: If a challenger has more keyword hits than the current top, it gets promoted.

### Example
```text
User: "متحصلات الموردين اخر 3 شهور"

Page Candidates:
  #1: PageID=304 (Score: 0.7183) Display="مدفوعات الموردين"     → 1 keyword hit ("الموردين")
  #2: PageID=2542 (Score: 0.6815) Display="متحصلات الدفعات"     → 1 keyword hit ("متحصلات")
  #3: PageID=246 (Score: 0.6747) Display="بيانات الموردين"     → 1 keyword hit ("الموردين")

✓ PageID=304 selected (top semantic + keyword tie)
```

---

## � Project Structure (Key Components)

```text
DynamicAi/
├── DynamicAi.Domain/                    # Core Domain Layer
│   ├── Entities/
│   │   ├── ConversationTurn.cs          # Context storage
│   │   ├── LlmUsageDetail.cs            # Usage logs
│   │   └── TenantBudget.cs              # Financial quotas
│   └── Enums/
│       └── QueryActionType.cs           # NEW/WRAP/REGENERATE
│
├── DynamicAi.Application/               # Business Logic Layer
│   ├── Interfaces/
│   │   ├── IQueryOrchestrator.cs
│   │   ├── IIntentClassificationService.cs
│   │   ├── ILlmUsageTracker.cs          # Cost Tracking Interface
│   │   ├── IRagRetrievalService.cs
│   │   ├── IDbSchemaProvider.cs         # 🆕 Schema Map Interface
│   │   ├── ISqlSanitizerService.cs      # 🆕 SQL Auto-Fixer Interface
│   │   └── IQueryCacheService.cs        # 🆕 QueryCache with EnsureInitializedAsync
│   ├── Services/
│   │   ├── QueryOrchestrator.cs         # Multi-step pipeline + Shared Embedding
│   │   ├── LlmUsageTracker.cs           # Token/USD Mapping Service
│   │   ├── BudgetLimiterService.cs      # Circuit Breaker Logic
│   │   ├── Handlers/                    # Decoupling Strategy
│   │   │   ├── SqlIntentHandler.cs
│   │   │   ├── ChatIntentHandler.cs
│   │   │   └── PredictIntentHandler.cs
│   │   ├── SqlEngine/                   # 🆕 SQL Processing Pipeline
│   │   │   ├── DbSchemaProvider.cs      #   Schema fetcher (sys.objects + cache)
│   │   │   ├── SanitizationVisitor.cs   #   Dual-purpose AST visitor
│   │   │   └── SqlSanitizerService.cs   #   Column surgeon (310 lines)
│   │   ├── VectorSearch/                # 🆕 Qdrant Integration
│   │   │   ├── QdrantRetrievalService.cs#   RAG + Smart Re-Ranking
│   │   │   └── QueryCacheService.cs     #   Semantic query cache
│   │   └── SqlGenerationService.cs
│
├── DynamicAi (API Host)/                # API Layer + Warmup Services
│   ├── Controllers/
│   │   ├── QueryController.cs
│   │   ├── MetricsController.cs         # Cost Dashboard Endpoints
│   │   └── QueryCacheController.cs
│   ├── Services/                        # 🆕 Background Warmup Services
│   │   ├── DbSchemaCacheWarmerService.cs#   Pre-fetches DB schema at startup
│   │   ├── IntentIndexWarmerService.cs  #   Pre-creates Qdrant intent indexes
│   │   └── QueryCacheWarmerService.cs   #   Pre-warms QueryCache indexes
│
├── DynamicAi.Infrastructure/            # Data Access Layer
│   └── Persistence/
│       └── Repositories/
│           ├── LlmUsageRepository.cs
│           └── TrainingExampleRepository.cs
│
└── DynamicAI.Tests/                     # Testing (104+ Tests)
    ├── Services/
    │   ├── LlmUsageTrackerTests.cs
    │   └── SqlGenerationServiceTests.cs
    └── Models/
```

---

## 🔮 Future Roadmap

### ✅ Recently Completed (v1.7.0) - "The Performance & UX Revolution"

| Feature | Status | Impact |
|:---|:---:|:---|
| **🚀 3x Warmup Services** | ✅ Done | **Zero Cold-Start:** Schema + Intent + QueryCache pre-warmed. |
| **⚡ Shared Embedding** | ✅ Done | **800-1300ms saved** per query via single embedding reuse. |
| **🗄️ RAG Context Cache** | ✅ Done | **240min TTL:** Eliminates repeat Vector DB lookups. |
| **⛔ Stop Button** | ✅ Done | **User Control:** Cancel generation mid-pipeline. |
| **🔬 SQL Sanitization** | ✅ Done | **Auto-Fixer:** Surgically removes hallucinated columns via AST. |
| **🎯 Smart Re-Ranking** | ✅ Done | **Arabic Keyword Tiebreaker:** Hybrid semantic + keyword page matching. |

### ✅ Previously Completed (v1.6.0) - "The Financial Hardening"

| Feature | Status | Impact |
|:---|:---:|:---|
| **💎 Cost Governance** | ✅ Done | **Zero Bill Shock:** 100% tracked call points. |
| **🛡️ Daily Budgeting** | ✅ Done | **Auto-Circuit Breaker:** Per-tenant USD limits. |
| **📊 Financial Dashboard** | ✅ Done | **Premium Analytics:** Real-time glassmorphic UI. |
| **M13: Auto-Capture** | ✅ Done | **Self-Learning:** Reliable Training data loop. |

### ✅ Previously Completed (v1.5.0)

| Feature | Status | Impact |
|:---|:---:|:---|
| **Triple-Layer Caching** | ✅ Done | Latency: 12.5s ➔ 1.8s |
| **Smart Cache Key** | ✅ Done | 100% Context Accuracy |
| **Context Decoupler** | ✅ Done | Zero Schema Hallucinations |
| **Token Efficiency** | ✅ Done | 96% Cost Reduction |
| **Hybrid Router** | ✅ Done | 4-Phase Architecture |
| **Context Data Loop** | ✅ Done | Multi-turn Memory |
| **Query Condenser** | ✅ Done | Smart Follow-ups |

### 🗺️ Next Phase: Advanced Intelligence & Hybrid Search �
*   **Goal:** Infinite Scalability & Precision
*   **Key Features:**
    *   **Hybrid Search Layer**: Combining Vector Search with Full-Text Keyword Search for 100% attribute recall.
    *   **Advanced Reranking**: Cross-Encoder integration to ensure the top-1 context is always mathematically perfect.
    *   **Self-Healing Schema**: Automatically detecting database changes and re-triggering partial vector refreshes.

### 📊 System Evaluation Summary (v1.7.0)

| Axis | Status | Score | Notes |
|:---|:---:|:---:|:---|
| **Latency** | ✅✅✅ | 10/10 | **Solved:** ~1.5s warm, ~3.3s cold (87% improvement). |
| **Cold Start** | ✅✅✅ | 10/10 | **Solved:** 3x Warmup Services eliminate cold penalty. |
| **Financial Governance**| ✅✅✅ | 10/10 | **Complete:** Daily budget + circuit breakers. |
| **Token Efficiency** | ✅✅✅ | 10/10 | **Solved:** 96% reduction via Smart Context Keys. |
| **Schema Accuracy** | ✅✅✅ | 10/10 | **Solved:** Context Decoupler. |
| **Hallucinations** | ✅✅ | 9/10 | **Minimized:** Clarification Loop + Validation. |
| **Intent Routing** | ✅✅✅ | 10/10 | **Complete:** Phases 0-3 + precomputed embedding. |
| **Observability** | ✅✅✅ | 10/10 | **Complete:** Real-time glassmorphism dashboards. |
| **UX Control** | ✅✅✅ | 10/10 | **Complete:** Stop Button + Smart Retry. |
| **Architecture** | ✅✅✅ | 10/10 | Clean Arch + Decoupled Handlers + Warmers. |
| **RAG Scaling** | ✅✅✅ | 9/10 | **Improved:** 240min TTL cache + shared embedding. |
| **SQL Sanitization** | ✅✅✅ | 10/10 | **Complete:** AST-based column pruning + alias resolution. |
| **Reranking** | ✅✅ | 7/10 | **Keyword Tiebreaker:** Arabic re-ranking for close scores. Cross-Encoder pending. |
| **Hybrid Search** | ✅ | 4/10 | **Partial:** Keyword tiebreaker active, full BM25 pending. |

---

## 📝 Changelog

### v1.7.0 (2026-03-02) - The Performance & UX Revolution 🚀
**"The Zero Cold-Start Milestone"**

**Performance & Startup Optimization:**
*   ✅ **DbSchemaCacheWarmer:** Background service that pre-fetches and caches all DB schema at startup.
*   ✅ **IntentIndexWarmer:** Background service that pre-creates Qdrant indexes for `intent_examples` collection.
*   ✅ **QueryCacheWarmer:** Background service that pre-initializes Qdrant indexes for `query_cache` collection.
*   ✅ **Shared Embedding:** Single embedding computation in Orchestrator, reused by IntentRouter + RAG Retrieval (saves ~800-1300ms/query).
*   ✅ **RAG Context Cache:** 240-minute TTL cache for page metadata — eliminates repeat Vector DB lookups.
*   ✅ **Result:** Cold start: 25s → 3.3s (87% ↓), Warm: 5.2s → 1.5s (72% ↓).

**UI/UX Enhancements:**
*   ✅ **Stop Button:** Full end-to-end pipeline cancellation — frontend aborts XHR, backend `CancellationToken` propagated to all async calls (Embedding, Intent, Condenser, SQL Gen, Execution). Controller returns HTTP 499 on cancellation.

**SQL Intelligence:**
*   ✅ **SQL Sanitization Layer:** `SqlSanitizerService` + `SanitizationVisitor` + `DbSchemaProvider` — auto-fixes hallucinated columns.
*   ✅ **Smart Re-Ranking:** Arabic-aware keyword tiebreaker in `QdrantRetrievalService` for page discovery accuracy.

**New Files:**
*   `DynamicAi/Services/QueryCacheWarmerService.cs` — BackgroundService for QueryCache index pre-warming.
*   `IQueryCacheService.EnsureInitializedAsync()` — Public startup initialization method.
*   `SqlEngine/SqlSanitizerService.cs` — Column surgeon (310 lines, 5 nested visitors).
*   `SqlEngine/SanitizationVisitor.cs` — Dual-purpose AST visitor (alias mapping + invalid column detection).
*   `SqlEngine/DbSchemaProvider.cs` — Schema fetcher (sys.objects + 10min cache).
*   `Interfaces/ISqlSanitizerService.cs` — Sanitizer interface.
*   `Interfaces/IDbSchemaProvider.cs` — Schema provider interface.

**Modified Files:**
*   `QueryCacheService.cs` — Made `EnsureCollectionExistsAsync` public as `EnsureInitializedAsync`; fixed `float.Parse`/`int.Parse` → `TryParse`.
*   `IQueryCacheService.cs` — Added `EnsureInitializedAsync` to interface.
*   `Program.cs` — Registered `QueryCacheWarmerService` + `SqlSanitizerService` + `DbSchemaProvider`.
*   `QdrantRetrievalService.cs` — Added Smart Re-Ranking with keyword tiebreaker logic.
*   `SqlIntentHandler.cs` — Integrated `SqlSanitizerService` in pipeline (Layer 3.5).
*   `chat.component.html` — Added Stop button with SVG icon.
*   `chat.component.ts` — Added `stopGeneration()` method with subscription cleanup.

---

### v1.6.0 (2026-02-12) - The Financial Governance & Reliability Update 🛡️
**"The Enterprise Maturity Milestone"**

**Governance & Financial Controls:**
*   ✅ **LLM Usage Tracker:** High-precision USD cost calculations across all AI handlers.
*   ✅ **Token Extraction Engine:** Deep SDK integration for 100% accurate token counting.
*   ✅ **Daily Budget Limiter:** Per-tenant USD limits with automatic circuit-breaking protection.
*   ✅ **Glassmorphism Cost Dashboard:** Real-time visual tracking of AI expenditure and efficiency.

**Reliability & Automation:**
*   ✅ **Training Auto-Capture (M13):** Asynchronous, race-condition-protected loop that captures successful SQL for the "Golden Dataset."
*   ✅ **ITrainingExampleRepository Expansion:** Added `ExistsByQueryLogIdAsync` to prevent duplicate data captures.

**UI/UX Enhancements:**
*   ✅ **Data Actions:** Integrated Print and Excel export functionality.
*   ✅ **Advanced Table Controls:** Horizontal scrolling fix and compact layout for data-heavy grids.
*   ✅ **Training Stats UI:** Real-time counters for Pending vs. Verified examples.

---

### v1.5.0 (2026-02-08) - The Performance & Intelligence Update ⚡
**"The Scalability Milestone"**

**Core Intelligence Improvements:**
*   ✅ **Triple-Layer Caching:** Integrated Semantic Router, RAG Cache, and Gemini Smart Cache for sub-2s latency.
*   ✅ **Smart Cache Key:** Context-aware hashing (`PageId + SchemaHash + TermCount`) ensures instant invalidation on schema changes.
*   ✅ **Context Decoupler:** Decoupled context retrieval to guarantee 100% accurate Schema Metadata mapping.
*   ✅ **Token Efficiency:** Reduced average token usage by 96% per cached query.

**New Capabilities (UI/UX):**
*   ✅ **Training Data Management:** Manual entry form for Golden Dataset + JSONL Export.
*   ✅ **Query Insights Tab:** Visual transparency for Latency, Handler Type, and Generated SQL.

**Fixes & Optimizations:**
*   ✅ **Refactored `QdrantRetrievalService`:** Clean separation of concerns between fetching and schema assembly.
*   ✅ **Updated `IRagRetrievalService`:** New signature for Smart Key generation.
*   ✅ **Cost Optimization:** Drastic reduction in LLM costs via aggressive caching strategies.

---

### v1.4.0 (2026-01-28) - The Self-Evolving Hybrid Router 🚀

**Major Architectural Shift**
- ✅ **Hybrid Router (Phases 0-3):** Implementation of the 4-Phase maturation strategy for intent routing.
- ✅ **Specialized Intent Handlers:** Decoupled `SqlIntentHandler`, `ChatIntentHandler`, and `PredictIntentHandler`.
- ✅ **Context-Aware Data Loop:** New `ConversationContextService` storing sampled results for trend analysis.
- ✅ **Multi-Step Prediction Pipeline:** `PredictionWithDataFetch` logic in Orchestrator for complex "Fetch-then-Predict" queries.

**Management & Incremental Seeding**
- ✅ **Incremental Intent Seeding:** `SeedMissingExamplesAsync` ensures new system intents are seeded without data loss.
- ✅ **New Controllers:** `QueryCacheController`, `DataSeederController` (Refactored), and updated `IntentReviewController`.
- ✅ **Admin Controls:** Endpoints for clearing cache and intent examples by natural question.

**v1.3.0 (2026-01-20) - The Brain & Observability**

### v1.2.0 (2026-01-07) - Query Condensing Release

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
- Total tests: 104 (up from 82)

---

## 📄 License

This project is proprietary software developed by **Dynamic Pro**.
