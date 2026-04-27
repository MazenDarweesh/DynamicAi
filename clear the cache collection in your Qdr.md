# clear the cache collection in your Qdrant Docker

To clear the cache collection in your Qdrant Docker instance, you can use the REST API via `curl` or PowerShell. 

Based on your project configuration, the cache collection is named **`query_cache`**.

### 1. Using `curl` (Command Line)
If you have `curl` installed, run this command to delete the collection entirely. The DynamicAi application will automatically recreate it with the correct settings (3072 dimensions, Cosine distance) the next time it starts or needs to cache something.

```bash
# Delete the collection entirely
curl -X DELETE "http://localhost:6333/collections/query_cache"
```

Alternatively, if you want to **keep the collection structure** but empty all the data:
```bash
# Clear all points but keep the collection
curl -X POST "http://localhost:6333/collections/query_cache/points/delete" \
     -H "Content-Type: application/json" \
     -d '{"filter": {}}'
```

### 2. Using PowerShell (Windows)
Since you are on Windows, you can use `Invoke-RestMethod`:

```powershell
# Delete the collection
Invoke-RestMethod -Method Delete -Uri "http://localhost:6333/collections/query_cache"
```

### 3. Using Docker Exec
If you want to run the command through the Docker container itself:
```bash
docker exec -it dynamicai-qdrant-local curl -X DELETE "http://localhost:6333/collections/query_cache"
```

---

### 🔍 Reference: Your Qdrant Collections
In case you intended to clear a different collection, here is the list found in your `appsettings.json`:

| Collection Name | Purpose |
| :--- | :--- |
| **`query_cache`** | Stores semantic cache for SQL queries (The "Cache"). |
| **`intent_examples`** | Stores examples for AI intent classification. |
| **`erp_knowledge_base_v3_indexed`** | The main RAG knowledge base for ERP documentation. |

> [!TIP]
> After deleting a collection, the application's `QueryCacheService` will log `[QueryCache] Creating collection: query_cache` on the next request to re-initialize it correctly.