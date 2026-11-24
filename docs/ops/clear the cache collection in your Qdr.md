> [!WARNING]
> **DEPRECATED (Phase 4 — Qdrant Removed):** Qdrant and embedding-based semantic query caching were completely removed from the DynamicAi runtime in Phase 4. DynamicAi v2.0 uses `ExactQueryCacheService` backed by an in-memory `IMemoryCache` (`MaxEntries = 10000`, `TTLDays = 7`).
>
> **Use these active API endpoints instead** (requires `Admin` role `X-Api-Key` header when `Auth:Mode = Strict`):
> - **Clear by question & scope:** `POST /api/query-cache/clear-by-question`
> - **Clear specific cache entry:** `DELETE /api/query-cache/{cacheId}`
> - **Clear all cached entries for a page:** `DELETE /api/query-cache/page/{pageId}?tenantId=...&companyId=...`
>
> Example (PowerShell):
> ```powershell
> Invoke-RestMethod -Method Delete -Uri "http://localhost:5053/api/query-cache/page/244" -Headers @{ "X-Api-Key" = "<YOUR_ADMIN_API_KEY>" }
> ```

---

# [Legacy Archive] Clear the Cache Collection in Qdrant Docker

To clear the legacy cache collection in a Qdrant Docker instance (for historical/legacy tool environments only), you can use the Qdrant REST API via `curl` or PowerShell.

Based on legacy configuration, the cache collection was named **`query_cache`**.

### 1. Using `curl` (Command Line)
```bash
# Delete the collection entirely
curl -X DELETE "http://localhost:6333/collections/query_cache"
```

Alternatively, empty all points while keeping the collection:
```bash
curl -X POST "http://localhost:6333/collections/query_cache/points/delete" \
     -H "Content-Type: application/json" \
     -d '{"filter": {}}'
```

### 2. Using PowerShell (Windows)
```powershell
Invoke-RestMethod -Method Delete -Uri "http://localhost:6333/collections/query_cache"
```

### 3. Legacy Qdrant Collections Reference

| Collection Name | Legacy Purpose |
| :--- | :--- |
| **`query_cache`** | Replaced by `ExactQueryCacheService` (`IMemoryCache`) in Phase 4. |
| **`intent_examples`** | Retired with legacy intent router removal. |
| **`erp_knowledge_base_v3_indexed`** | Retired from runtime; legacy schema tools are isolated in `tools/legacy/SchemaTools.sln`. |
