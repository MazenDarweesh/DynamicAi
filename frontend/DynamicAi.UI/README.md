# DynamicAi UI — Angular 21 POC Tester & Admin Console

The frontend provides an interactive Proof-of-Concept tester for `POST /AIQuery/integration/directed-page-ask` alongside operational, analytics, training-data, and LLM cost dashboards.

---

## 🧭 Routes & Modules

| Route | Component | Purpose |
| :--- | :--- | :--- |
| `/chat` | `ChatComponent` + `PocContextPanelComponent` | Directed-Page POC tester (defaults to Page 244 preset, editable `explicitContext` JSON, `comID`, `tenantId`, `tenantKey`, `connectionString`, cache clear actions, SQL/Excel export, token & cost badges). |
| `/dashboard` | `DashboardComponent` | Operational metrics, latency, cache hit ratio, and intent distribution (`AdminOnly`). |
| `/query-insights` | `QueryInsightsPageComponent` | Query history inspection and failure/SQL review (`AdminOnly`). |
| `/training-data` | `TrainingDataComponent` | Column synonyms, golden examples, and cache invalidation by question (`AdminOnly`). |
| `/costs` | `CostDashboardComponent` | Daily LLM budget (`$2.00/day`), token breakdown, and recent usage logs (`AdminOnly`). |

---

## 🔐 Strict Auth (`X-Api-Key` Header Bar)

The app shell (`src/app/app.html`) includes a global `X-Api-Key (POC)` pill in the top header backed by `PocSettingsService` (`localStorage`) and `apiKeyInterceptor`.

- Use an **Integration** or **Admin** key for `/chat` (`POST /AIQuery/integration/directed-page-ask`).
- Use an **Admin** key to load `/dashboard`, `/query-insights`, `/training-data`, `/costs`, and cache-clearing actions (`401`/`403` errors show inline guidance).

---

## 🔌 Dev Proxy (`proxy.conf.json`)

When running locally with `npm start`, Angular forwards API requests to the local .NET backend (`http://localhost:5053`):

- `/api` -> `http://localhost:5053`
- `/AIQuery` -> `http://localhost:5053`
- `/swagger` -> `http://localhost:5053`

---

## 🚀 Commands

Run from `frontend/DynamicAi.UI` (or pass `--prefix frontend/DynamicAi.UI` from repo root):

```powershell
# Install dependencies
npm install --legacy-peer-deps

# Start local dev server at http://localhost:4200
npm start

# Production build (outputs to dist/DynamicAi.UI/browser)
npm run build
```
