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
A standalone utility that treats the database schema as a "Knowledge Base".
*   **Extraction:** Reads table and view definitions from SQL Server.
*   **Embedding:** Converts schema definitions into vector embeddings.
*   **Storage:** Uploads these embeddings to `Qdrant` so the AI can "understand" the database structure semantically.

### 2. Dynamic AI Core (`DynamicAi.Application`)
The brain of the operation, containing the business logic.
*   **`QueryOrchestrator`:** Manages the flow of a user request.
*   **`SqlGenerationService`:** Constructs precise SQL queries based on user prompts and retrieved schema context.
*   **`QueryCacheService`:** *[MVP Feature]* Checks Qdrant for semantically similar past questions to return cached SQL instantly, bypassing the LLM for faster responses.

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

#### 1. Start Qdrant
Run Qdrant using Docker:
```bash
docker run -p 6333:6333 -p 6334:6334 qdrant/qdrant
```

#### 2. Configure Backend
Navigate to `DynamicAi.Api` and update `appsettings.json` or use User Secrets:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=...;Database=...;"
  },
  "Gemini": {
    "ApiKey": "YOUR_GEMINI_API_KEY"
  },
  "Qdrant": {
    "Host": "localhost",
    "Port": 6334
  }
}
```

#### 3. Run Schema Bootstrapper (First Time Only)
Initialize the vector knowledge base:
```bash
cd SchemaBootstrapper
dotnet run
```

#### 4. Start the Backend API
```bash
cd DynamicAi.Api
dotnet run
```
The API will be available at `https://localhost:7193` (or similar).

#### 5. Start the Frontend UI
```bash
cd DynamicAi.UI
npm install
npm start
```
Navigate to `http://localhost:4200` to use the application.

---

## 🌟 Current Phase: MVP Caching

We are currently in the **MVP Caching** phase (Branch: `Dy_AI_MVP_Caching`).

### ✅ Implemented Features
*   **Semantic Caching:** Queries are hashed and compared using vector similarity. If a user asks "Show me listing of pages" and someone else previously asked "List all pages", the system recognizes the semantic match and reuses the generated SQL.
*   **Context-Aware SQL Generation:** The system retrieves only relevant tables/views based on the user's question, reducing token usage and hallucination.
*   **Basic Chat UI:** Functional interface for sending messages and viewing tabular results.

### 🚧 Caching Logic Details
*   **Similarity Threshold:** Configurable (default `0.92`).
*   **TTL (Time To Live):** Cache entries expire after 7 days (default).
*   **Payload:** Stores `original_question`, `generated_sql`, and `tenant_id` to ensure data isolation.

---

## 🔮 Future Roadmap

Based on the **RAG Production Evaluation** (7 Layers & 6 Pillars), here is the prioritized roadmap to move from **MVP (Current)** to **Scale**.

### 🗺️ Development Phases

#### 1️⃣ Phase 1: MVP (Completed) ✅
*   **Goal:** Functional Foundation
*   **Status:** Stable
*   **Key Features:**
    *   [x] Vector Database (Qdrant)
    *   [x] Embedding (Gemini)
    *   [x] SQL Generation
    *   [x] Basic Semantic Caching
    *   [x] Clean Architecture

#### 2️⃣ Phase 2: Hardening (Months 2-3) 🔴 [High Priority]
*   **Goal:** Reliability & Safety (Fixing "Silent Failures" & "Hallucinations")
*   **Key Features:**
    *   **Reranking Layer:** Implement `IRerankingService` to re-score top-K documents (Reduces hallucinations by ~30%).
    *   **SQL Validation Layer:** Pre-execution validation to detect dangerous or invalid queries ("Silent Failures").
    *   **User Quotas:** Implement rate limiting and cost controls per tenant ("Cost Explosion").
    *   **Streaming Responses:** Improve UX latency perception ("Latency Blindspot").

#### 3️⃣ Phase 3: Intelligence (Months 3-4) 🟡
*   **Goal:** Higher Recall & Context Quality
*   **Key Features:**
    *   **Hybrid Search:** Combine Vector Search + Keyword Search for exact matches (improves recall by ~20%).
    *   **Smart Chunking:** Implement document-aware splitting in `SchemaReaderService`.
    *   **Confidence Scoring:** Expose AI confidence levels to the user.

#### 4️⃣ Phase 4: Observability (Months 4-5) 🟡
*   **Goal:** Production Monitoring
*   **Key Features:**
    *   **Tracing:** OpenTelemetry integration for full request visibility.
    *   **Cost Dashboard:** real-time monitoring of token consumption.
    *   **Quality Metrics:** Automated evaluation of faithfulness and context recall.

#### 5️⃣ Phase 5: Scale (Months 5-6) ⚪
*   **Goal:** Massive Scale Support
*   **Key Features:**
    *   **Embedding Fine-tuning:** Customize models for domain-specific ERP terminology.
    *   **Scaling Tests:** Validation for 100K+ schema objects.
    *   **Prompt Versioning:** Manage `Prompt Drift` with versioned registries.

### 📊 System Evaluation Summary

| Axis | Status | Score | Notes |
|:---|:---:|:---:|:---|
| **Vector DB** | ✅✅✅ | 9/10 | Qdrant with proper payload indexing is solid. |
| **Embedding** | ✅✅ | 8/10 | Gemini models are performing well. |
| **Architecture** | ✅✅ | 8/10 | Clean Architecture & Semantic Kernel are excellent choices. |
| **Prompt Engineering** | ✅✅ | 7/10 | Modular static rules preventing excessive drift. |
| **Cost Control** | ✅✅ | 7/10 | Good start with Implicit Cache optimization. |
| **Doc Parsing** | ✅ | 6/10 | SMO-based SQL parsing is accurate but limited to SQL. |
| **RAG Scaling** | ⚠️ | 6/10 | Needs validation for larger datasets. |
| **Chunking** | ⚠️ | 5/10 | Needs "Smart Chunking" for better context. |
| **Latency** | ⚠️ | 5/10 | Needs Streaming & more client-side handling. |
| **Silent Failures** | ⚠️ | 4/10 | **Critical:** Needs SQL Validation Layer. |
| **Observability** | ⚠️ | 3/10 | Basic logging exists; needs Tracing & Metrics. |
| **Hybrid Search** | ❌ | 2/10 | **Missing:** Pure vector search misses exact keywords. |
| **Reranking** | ❌ | 0/10 | **Critical:** Major cause of hallucinations. |---

