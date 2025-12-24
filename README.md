# Dynamic AI 🧠

> **Natural Language to SQL Generation with Semantic Context & Caching**

Dynamic AI is an intelligent system designed to bridge the gap between natural language questions and complex database queries. It leverages **Google Gemini** for LLM capabilities and **Qdrant** for vector-based semantic search, allowing users to query their data using everyday language while ensuring high performance through intelligent caching.

---

## 🏗️ Architecture Overview

The system follows **Clean Architecture** principles and is divided into distinct layers to ensure maintainability and scalability.

```text
     User 
      ↕ 
[DynamicAi UI (Angular)] 
      ↕ 
[DynamicAi API (.NET)] ──────┐
                             │
            ┌────────────────▼────────────────┐
            │       Query Orchestrator        │
            └───────┬────────┬────────┬───────┘
                    │        │        │
          ┌─────────▼─┐  ┌───▼──┐  ┌──▼─────┐
          │Query Cache│  │SQLGen│  │Schema  │
          └─────┬─────┘  └──┬───┘  └──┬─────┘
                ↕           ↕         │
         [(Qdrant DB)]   [Gemini]  [(SQL DB)]
```

### 🛠️ Technology Stack

*   **Backend:** .NET 8 (C#)
*   **Frontend:** Angular 17+ (TypeScript, SCSS)
*   **Vector Database:** Qdrant (for schema embeddings & query caching)
*   **LLM:** Google Gemini (via AI API)
*   **Database:** SQL Server
*   **Containerization:** Docker (for Qdrant)

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
*   ✅ **Schema CDC:** Only uploads changed Tables/Views (incremental updates).
*   ⚠️ **Data CDC:** Infrastructure built (`CdcChangeStrategy`), awaiting activation.
*   ⚠️ **Multi-Tenancy:** `tenant_id` support planned (Module 1 in roadmap).

**Components:**
*   `SchemaOrchestrator.cs` - Main orchestrator (350 lines)
*   `CdcChangeStrategy.cs` - SQL Server CDC integration (built, not wired)
*   `QdrantVectorDbService.cs` - Gemini embedding + Tier 1 rate limiting

### 2. Dynamic AI Core (`DynamicAi.Application`)
The brain of the operation, containing the business logic.
*   **`QueryOrchestrator`:** Manages the flow of a user request.
*   **`SqlGenerationService`:** Constructs precise SQL queries based on user prompts and retrieved schema context.
*   **`QueryCacheService`:** *[MVP Feature]* Checks Qdrant for semantically similar past questions to return cached SQL instantly, bypassing the LLM for faster responses.
*   **`GeminiCostTracker`:** *[New]* Centralized service that tracks token usage and calculates costs for Chat and Embedding models in real-time.

### 3. Dynamic AI UI (`DynamicAi.UI`)
A modern, responsive web interface built with **Angular (Latest)**.
*   **Chat Interface:** A ChatGPT-like experience for interacting with the data.
*   **Data Visualization:** Displays query results in clean, formatted tables.
*   **Feedback Loop:** Allows users to "dislike" or retry queries, feeding back into the system improvement loop.

---

## 🚀 Getting Started

### Prerequisites
*   [Docker Desktop](https://www.docker.com/products/docker-desktop) (for running Qdrant)
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

### 🔧 Troubleshooting
*   **"Unknown Error" in Chat:** This usually means the browser blocked the backend SSL certificate. Run `dotnet dev-certs https --trust` and restart your browser/use Incognito.
*   **Qdrant HTTP/2 Protocol Error:** Ensure your `Program.cs` is using the URI-based `QdrantClient` constructor with port `6334` for Cloud instances.
*   **`ng` not recognized:** Use `npm start` instead of `ng serve`.

---

## 🌟 Current Phase: MVP Caching

We are currently in the **MVP Caching** phase (Branch: `Dy_AI_MVP_Caching`) with **Observability Layer** integrated.

### ✅ Implemented Features

#### 1. Core Logic & Caching
*   **Semantic Caching:** Queries are hashed and compared using vector similarity (Qdrant).
*   **Context-Aware SQL Generation:** Retrieves irrelevant tables/views to reduce token usage.
*   **Clean Architecture:** Strict separation of concerns (Domain, Application, Infrastructure, API).

#### 2. Reliability & Safety
*   **SQL Validation Layer:** `SecurityVisitor` & `Rewriter` ensure generated SQL is safe and optimized. **(Verified by 62 Unit Tests ✅)**.
*   **Retry Policies:** Polly integration for resilient API calls.

#### 3. Observability & Cost Tracking
*   **Cost Tracker (`GeminiCostTracker`):** Real-time tracking of input/output tokens and cost calculation for every request.
*   **Structured Logging (`Serilog`):** Unified JSON-ready logs across all services for seamless monitoring.
*   **Request Metrics:** Tracks latency (`TotalDuration`) and Status Codes.

#### 4. Testing Strategy
*   **Unit Tests:** Extensive coverage for `SqlValidatorService`, `SqlRewriterService`, and `SqlGenerationService`.
*   **Integration Tests:** Verifies the full pipeline from `QueryOrchestrator` to `Gemini`.
*   **Mocking:** Uses `FakeItEasy` for reliable dependency simulation.

### 🚧 Caching Logic Details
*   **Similarity Threshold:** Configurable (default `0.92`).
*   **TTL (Time To Live):** Cache entries expire after 7 days (default).
*   **Payload:** Stores `original_question`, `generated_sql`, and `tenant_id` to ensure data isolation.

---

## 🔮 Future Roadmap

Based on the **RAG Production Evaluation** (7 Layers & 6 Pillars), here is the prioritized roadmap to move from **MVP (Current)** to **Scale**.

### 🗺️ Development Phases

#### 1️⃣ Phase 2: Hardening (Months 2-3) 🔴 [High Priority]
*   **Goal:** Reliability & Safety (Fixing "Silent Failures" & "Hallucinations")
*   **Key Features:**
    *   **Reranking Layer:** Implement `IRerankingService` to re-score top-K documents (Reduces hallucinations by ~30%).
    *   **User Quotas:** Implement rate limiting and cost controls per tenant ("Cost Explosion").
    *   **Streaming Responses:** Improve UX latency perception ("Latency Blindspot").

#### 2️⃣ Phase 3: Intelligence (Months 3-4) 🟡
*   **Goal:** Higher Recall & Context Quality
*   **Key Features:**
    *   **Hybrid Search:** Combine Vector Search + Keyword Search for exact matches (improves recall by ~20%).
    *   **Smart Chunking:** Implement document-aware splitting in `SchemaReaderService`.
    *   **Confidence Scoring:** Expose AI confidence levels to the user.

#### 3️⃣ Phase 4: Advanced Observability (Months 4-5) 🟡
*   **Goal:** Deep Production Monitoring
*   **Key Features:**
    *   **Tracing:** OpenTelemetry integration for full request visibility.
    *   **Quality Metrics:** Automated evaluation of faithfulness and context recall.

#### 4️⃣ Phase 5: Scale (Months 5-6) ⚪
*   **Goal:** Massive Scale Support
*   **Key Features:**
    *   **Embedding Fine-tuning:** Customize models for domain-specific ERP terminology.
    *   **Scaling Tests:** Validation for 100K+ schema objects.
    *   **Prompt Versioning:** Manage `Prompt Drift` with versioned registries.

### 📊 System Evaluation Summary

| Axis | Status | Score | Notes |
|:---|:---:|:---:|:---|
| **Vector DB** | ✅✅✅ | 9/10 | Qdrant with proper payload indexing is solid. |
| **Silent Failures** | ✅✅ | 9/10 | **Solved:** SQL Validation Layer (62 Tests) catches most errors. |
| **Embedding** | ✅✅ | 8/10 | Gemini models are performing well. |
| **Architecture** | ✅✅ | 8/10 | Clean Architecture & Semantic Kernel are excellent choices. |
| **Observability** | ✅✅ | 8/10 | Cost tracking & Structured logging implemented. |
| **Prompt Engineering** | ✅✅ | 7/10 | Modular static rules preventing excessive drift. |
| **Cost Control** | ✅✅ | 7/10 | Good start with Implicit Cache optimization. |
| **Doc Parsing** | ✅ | 6/10 | SMO-based SQL parsing is accurate but limited to SQL. |
| **RAG Scaling** | ⚠️ | 6/10 | Needs validation for larger datasets. |
| **Chunking** | ⚠️ | 5/10 | Needs "Smart Chunking" for better context. |
| **Latency** | ⚠️ | 5/10 | Needs Streaming & more client-side handling. |
| **Hybrid Search** | ❌ | 2/10 | **Missing:** Pure vector search misses exact keywords. |
| **Reranking** | ❌ | 0/10 | **Critical:** Major cause of hallucinations. |
