# DynamicAI 🧠

> **The Enterprise-Grade Predictive Insight Engine & Agentic SQL Router**

**Dynamic AI** is a production-ready, full-stack AI solution that transforms natural language into optimized, secure SQL queries and forecasts future business trends. Built on a robust **Clean Architecture** (C#/.NET Core & Angular), it bridges the gap between raw data and business users using **Google Gemini** (LLM) and **Qdrant** (Vector Search).

Unlike simple text-to-SQL wrappers, Dynamic AI is an **Intelligent System** featuring an **Agentic Intent Routing Engine**, **Triple-Layer Caching**, **Enterprise Cost Governance**, and a **Self-Healing SQL Validation Loop** that actively recovers from LLM hallucinations.

---

## 🏗️ Architecture Overview

Designed a multi-stage SQL generation pipeline with intent routing, semantic caching, RAG retrieval, AST-based validation, and self-healing sanitization. 

```text
     User (Business / Analyst)
      ↕
[DynamicAi UI (Angular 17+)]  ◄── 🆕 Stop Button (Cancel mid-pipeline)
      ↕
[DynamicAi API (.NET 8 Clean Arch)] ───────┬────────────────────────────┐
      │                                    │                            │
      │  🆕 STARTUP WARMUP LAYER           │  🆕 DIRECT PAGE BYPASS      │
      │  (DbSchemaCacheWarmer +            │  (Integration Endpoint)     │
      │   IntentIndexWarmer +              │  (Skips RAG & Router)      │
      │   QueryCacheWarmer)                │            │               │
      │                                    │            │               │
                  ┌────────────────────────▼────────────┼────────────┐  │
                  │   💎 FINANCIAL & TELEMETRY OBSERVABILITY LAYER   │  │
                  │      (LlmUsageTracker + Daily Budget Limiter)    │  │
                  └────────────────────────┬────────────┼────────────┘  │
                                           │            │               │
                  ┌────────────────────────▼────────────▼────────────┐  │
                  │              Unified Query Handler               │  │
                  │  (Central Engine + 🆕 Prompt Partials)           │  │
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
                               │🆕Layer 3.8: Hardener  │                  │
                               │ (Iron Dome Rewriter)  │                  │
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

### 🆕 Directed Page Integration (RAG Bypass)
*   **Endpoint:** `POST /AIQuery/integration/directed-page-ask`
*   **Flow:** This high-performance path allows external ERP pages to inject their own schema context (`ExplicitContext`) and connection strings directly. 
*   **Result:** The system skips **Layer 0 (Router)** and **Layer 2 (RAG Retrieval)**, anchoring the LLM immediately to the provided metadata for sub-second responses.

---

## 🌊 The Unified Intelligence Flow (All Paths)

The `UnifiedQueryHandler` is the centralized brain of DynamicAI. It manages a multi-stage pipeline with built-in self-healing and defensive layers.

### 1. The Fast-Track Path (Cache Hits)
*   **Layer 0: Global Cache Hit**: If the exact query embedding exists in the global cache, the system executes the SQL directly, bypassing RAG and LLM entirely.
*   **Layer 1.5: Page-Specific Cache Hit**: If the global search misses, but a match is found after resolving the `PageID` (Post-RAG), the system triggers the cached SQL for that specific context.

### 2. The Context Discovery Path (RAG)
*   **Directed Context (Bypass)**: If the request includes `InjectedContext` (Direct Page), RAG is bypassed. The system anchors specifically to the provided Page/Schema.
*   **Semantic Retrieval**: Default mode. Qdrant performs semantic search + Smart Re-Ranking to fetch the most relevant Page metadata.

### 3. The Generation & Self-Healing Path
*   **Prompt Construction**: Modular partials are injected into `unified_master`, including current DateTime, Tenant filters, and Schema JSON.
*   **JSON Parse Recovery**: If the LLM returns invalid JSON, the handler automatically triggers a **Correction Call** to fix the syntax.
*   **SQL Execution Retry Loop**: If the generated SQL fails due to Sanitization errors (hallucinated columns) or Database errors, the system performs up to **2 Self-Healing Retries**, sending the exact error log back to the LLM, forcing it to rewrite the query correctly without user intervention.

### 4. The Hardening Path (Iron Dome 2.0)
*   **Validation**: AST-level check for unauthorized DDL/DML using Microsoft ScriptDom.
*   **Sanitization**: Column auto-fixer prunes hallucinated columns against real schema.
*   **Rewriting**: `SqlRewriterService` + `SqlHardeningVisitor` inject mandatory `TailCondition` filters with strict alias scoping (`T1.`, `T2.`, etc.).

---

## 🚀 Key Technical Pillars

1.  **Full-Stack & Production Ready**
    *   **Frontend:** Modern Angular 17+ with Material Design, Markdown rendering, and Syntax Highlighting.
    *   **Backend:** .NET 8 Web API following **Clean Architecture** (Domain, Application, Infrastructure, Api).
    *   **Database:** SQL Server (Target) + Qdrant (Vector Intelligence).
    *   **Observability:** Real-time dashboards, structured logging, and cost tracking.

2.  **Triple-Layer Caching Architecture (The "Speed" Layer)**
    *   **Semantic Cache:** Instantly returns semantically identical questions (e.g., "Show sales" == "Revenue").
    *   **RAG Context Cache:** Eliminates redundant Vector DB lookups for schema metadata.
    *   **LLM Response Cache:** Leverages Gemini's Context Caching to bypass generation for known schema states.

3.  **Agentic Intent Routing Engine (The "Brain" Layer)**
    *   **Zero-Shot Classification:** Uses Vector Search to route queries to the correct handler (`Chat`, `DataRetrieval`, `Prediction`, `Analysis`).
    *   **Human-in-the-loop (HITL):** Administrators can manually approve or reject ambiguous intents in the Admin Dashboard to continuously train the system.

4.  **Defense-in-Depth Security & Anti-Hallucination**
    *   **Validation Layer:** Uses Microsoft's `ScriptDom` to parse and efficiently block any DML (INSERT/UPDATE/DELETE) or DDL (DROP/ALTER) commands before execution.
    *   **SQL Sanitization Layer:** `SanitizationVisitor` + `SqlSanitizerService` surgically removes hallucinated columns from LLM-generated SQL using AST analysis.
    *   **Read-Only Context:** The LLM is never given direct write access credentials.

5.  **Smart Re-Ranking (Hybrid RAG)**
    *   **Semantic + Keyword:** Combines Qdrant vector search with Arabic-aware keyword tiebreaking for 100% page accuracy.
    *   **Score Delta Guard:** Only re-ranks when candidates are within 0.05 cosine distance.

6.  **Financial Governance & ROI Tracking**
    *   **Token Accuracy:** Full extraction of actual tokens from Gemini API.
    *   **Daily Budget Limiter:** Per-tenant USD limits with automatic circuit-breaking protection to prevent API bill shock.

### 🛠️ Technology Stack

*   **Backend:** .NET 8 (C#)
*   **Frontend:** Angular 17+ (TypeScript, SCSS)
*   **Vector Database:** Qdrant (for schema embeddings & query caching)
*   **LLM:** Google Gemini (via Semantic Kernel)
*   **Database:** SQL Server + EF Core (QueryLogs, TrainingExamples)
*   **Containerization:** Docker (Qdrant, API, UI)

---

## 🛡️ Overcoming AI Hallucinations: The "Iron Dome" Strategy

> **"You can't 'fix' hallucinations; you must engineer around them."**

During initial architecture, a major assumption was made: dumping the entire raw database schema into the LLM prompt would allow it to write accurate SQL. This proved highly misleading. The massive, unfiltered context confused the LLM, leading to severe hallucinations and invalid table joins. 

To solve this, Dynamic AI treats the LLM as a probabilistic component wrapped in a deterministic "Iron Dome" defense system. We employ an **8-Layer Defense-in-Depth Strategy** to ensure enterprise-grade reliability:

### 1. Context Grounding (The RAG Layer)
*   **Mechanism:** Instead of dumping the entire schema, `QdrantRetrievalService` performs a multi-step semantic search with **Smart Re-Ranking**. The LLM only receives strictly relevant table definitions, completely eliminating context-overload hallucinations.

### 2. Strict Schema Mapping (The Constraint Layer)
*   **Mechanism:** `SqlGenerationService` uses modular prompts with strict rules:
    *   **Rule 3:** "ALLOWED TABLES ONLY... Any other table/view=FORBIDDEN."
    *   **Rule 4:** "Resolve columns ONLY via ListSetups... NEVER GUESS."

### 3. Post-Generation Validation (The Guard Layer)
*   **Mechanism:** Before execution, `SqlValidatorService` parses the raw SQL using Microsoft's **ScriptDom** (AST Parser) to strictly block `DELETE`, `UPDATE`, `DROP`, and other DDL/DML vectors.

### 4. SQL Sanitization (The Auto-Fixer Layer)
*   **Mechanism:** `SqlSanitizerService` + `SanitizationVisitor` use AST analysis to surgically prune invalid columns from `SELECT`, `WHERE`, `ORDER BY`, and `GROUP BY` — then rebuild clean SQL.

### 5. Self-Healing Retry Loop (The Correction Layer)
*   **Mechanism:** When the LLM confidently hallucinates an invalid column, the sanitizer blocks it and throws a `SqlSanitizationException`. The `UnifiedQueryHandler` catches this error (e.g., *"Column X does not exist"*) and automatically feeds it back to the LLM, forcing the AI to acknowledge its mistake and rewrite the query correctly without user intervention.

### 6. Smart Re-Ranking (The Precision Layer)
*   **Mechanism:** Uses a **Two-Stage Search Strategy**. Phase 1 identifies candidates via Vector Search. Phase 2 applies a keyword tiebreaker (`PageDisplay` Arabic name) for 100% accuracy.

### 7. Deterministic SQL Hardening (Iron Dome 2.0)
*   **Mechanism:** Uses **Scoped Injection** via `SqlHardeningVisitor`. It ensures mandatory system filters (e.g., `T1.IsResale=0`) are only injected where missing, respecting table aliases.

### 8. Temporal Intelligence (Temporal Context Layer)
*   **Mechanism:** `UnifiedQueryHandler` automatically injects the current server date and time into every prompt context for time-relative queries.

---

## 💎 Enterprise LLM Cost Governance (Financial Guardrails)

> **"AI is only as good as its ROI. We don't just track costs; we control them."**

In v1.6.0, Dynamic AI introduced a sophisticated **LLM Financial Governance System**, transforming unpredictable API bills into a predictable, manageable business expense.

### 🛡️ The Financial "Iron Dome" Architecture

We've implemented a **Non-Bypassable Cost Tracking & Limiting Layer** integrated across 16 critical AI call points:

1.  **Precision Unit Tracking (Token Extraction)**: Uses the official SDKs to extract *actual* token usage from every raw response.
2.  **Real-time USD Financial Mapping**: Maps tokens to current model pricing with **8-decimal USD precision**.
3.  **Active Circuit Breakers (Daily Budgeting)**: Every Tenant has a **Daily USD Budget**. Once reached, the system triggers an automatic **Circuit Breaker**.
4.  **Glassmorphism Financial Dashboard**: A premium, real-time UI that visualizes burn rate, efficiency ratios, and multi-tenant usage.

---

## 📄 License

This project is proprietary software developed by **Dynamic Pro**.
