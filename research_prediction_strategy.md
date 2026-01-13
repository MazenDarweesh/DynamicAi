# 🌳 Tree of Thought (ToT): Predictive & Analytic Capabilities Strategy
## Research & Analysis for Dynamic AI

> **Objective:** Expand `DynamicAi` from a pure "Text-to-SQL" system to a "Predictive Insight Engine" that can handle future-looking questions (e.g., "Will sales increase?") or deep analysis (e.g., "Why did churn spike?").

---

## 🧐 The Root Problem
**Current State:**
- Input: "What were the sales last month?"
- Process: Text → SQL → Database → RowSet.
- Output: Exact historical data.

**The Gap:**
- Input: "Will sales go up next month?"
- Process: Text → SQL (`SELECT * FROM Future`??) → ❌ **Error/Empty**.

We need a mechanism to **bridge** historical data (SQL) with future intelligence (LLM/ML).

---

## 🌿 Branch 1: The "Analyst" Post-Processor (Low complexity)
**Concept:**
Don't change the pipeline. Just add a "brain" at the end. The SQL gets the *history*, and the LLM "extrapolates" it.

*   **Workflow:**
    1.  User: "Predict next month's sales."
    2.  LLM (Layer 0.5): Rewrites to "Get sales for last 12 months" (History).
    3.  SQL Layer: Executes `SELECT month, sales FROM ...`.
    4.  **🆕 Layer 6 (Analyst):** Takes the 12 rows of data → uses LLM to spot trend → "Based on the 10% upward trend, next month looks like..."

*   **Pros:**
    *   ✅ **Fastest Implementation:** Uses existing SQL pipeline.
    *   ✅ **No New Infrastructure:** No Python ML services needed.
    *   ✅ **Explainable:** "I looked at past data X and projected Y."

*   **Cons:**
    *   ⚠️ **Weak Math:** LLMs are bad at precise arithmetic/regression.
    *   ⚠️ **Context Window:** Can't feed 10,000 rows for deep pattern matching.

---

## 🌿 Branch 2: The "Router" Architecture (Medium Complexity)
**Concept:**
Evolve `QueryCondenser` (Layer 0.5) into a smart **Central Dispatcher**.

*   **Workflow:**
    1.  User: "Predict sales..."
    2.  **🆕 Intent Classifier:** Detects `Intent.Prediction`.
    3.  **Router:** Switches tracks!
        *   If `SQL`: Continue to `SqlGenerationService`.
        *   If `Prediction`: Route to `ForecastingService`.
    4.  `ForecastingService`: A specialized service that might use a TimeSeries plugin or a specific Gemini model tuned for data analysis.

*   **Pros:**
    *   ✅ **Clean Architecture:** Strict separation of concerns (SQL logic vs. ML logic).
    *   ✅ **Scalable:** Can swap the "Prediction Engine" later without breaking SQL.
    *   ✅ **Best Practice:** aligns with "Agentic Router Patterns" (Microsoft Agent Framework).

*   **Cons:**
    *   ⚠️ **Latency:** Two LLM hops (Router + Agent).
    *   ⚠️ **Complexity:** Need to build the `ForecastingService`.

---

## 🌿 Branch 3: Native Function Calling (High Complexity/Power)
**Concept:**
Give the LLM "tools" (Tools) and let it drive.

*   **Workflow:**
    1.  User: "Analyze sales."
    2.  Gemini: "I have tools: `[get_sql_data, run_forecast_model]`."
    3.  Gemini: Decides to call `get_sql_data` first to get history.
    4.  Gemini: Decides to call `run_forecast_model` with that history.
    5.  Gemini: Synthesizes final answer.

*   **Pros:**
    *   ✅ **True Agentic Behavior:** Handling multi-step reasoning natively.
    *   ✅ **Flexible:** Can handle "Get data, then forecast, then email me."

*   **Cons:**
    *   ⚠️ **Non-Deterministic:** Harder to debug/control than a strict pipeline.
    *   ⚠️ **Token Cost:** Function calling loops can eat tokens.

---

## 🏆 Recommendation: "The Hybrid Router"
**Path:** Combine **Branch 2 (Router)** with specific **Branch 1 (Analyst)** capabilities.

### Why?
Our project is built on **Clean Architecture**. A Router fits perfectly as a "Gateway" in the Application Layer. It keeps the SQL pipeline pure while opening a door for "Advanced Analytics".

### The Proposed "Module 2.5" Upgrade:

1.  **Upgrade Layer 0.5:** Rename `QueryCondenser` to `IntentOrchestrator`.
2.  **Define Intents:**
    *   `DataRetrieval` (The current path)
    *   `DataAnalysis` (Why did X happen?)
    *   `Forecasting` (What will happen?)
3.  **Build `AnalysisService`:**
    *   Unlike a full ML rig, this service simply:
    *   Generates "Supportive SQL" (e.g., get last 6 months).
    *   Passes results + Question to a **Reasoning LLM**.

### 🚀 Immediate Action Plan (ToT Outcome)
1.  **Modify `CondenserResult`:** Add `Intent` property.
2.  **Update Prompt (Layer 0.5):** Teach it to recognize "Prediction" vs "Retrieval".
3.  **Create `AnalysisService` Interface:** Placeholder for the new track.

**Final Verdict:** We do NOT need a heavy Python/ML stack yet. We can achieve 80% of "Prediction" value using **LLM-based extrapolation of SQL results** (The Hybrid Approach).
