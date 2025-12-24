# [Roadmap V3.0: Qdrant Scaling & Multi-Tenancy Architecture]
> **Last Updated:** 2024-12-24 | **Status:** Final - Ready for Execution

---

## 1. Goals & Objectives

| Goal | Description |
|:---|:---|
| **توفير المساحة** | تخزين الفرق فقط (Delta)، وليس نسخة كاملة لكل عميل |
| **تسريع التسطيب** | عميل جديد = رفع التغييرات فقط (إن وجدت) |
| **عدم التكرار** | الـ Schema والـ Metadata المشتركة تُخزن مرة واحدة |
| **عزل العملاء** | كل عميل يرى بياناته فقط + البيانات العامة |

---

## 2. Capacity Analysis

| Metric | Current (2 Pages) | Projected (20+ Pages) | Limit |
|:---|:---|:---|:---|
| **Points** | 333 | ~3,500 | Unlimited |
| **Disk** | ~240 MB (overhead) | ~400-500 MB | 4 GB ✅ |
| **RAM** | ~316 MB | ~400 MB | 1 GB ✅ |

**Conclusion:** Free Tier is sufficient for 20+ pages expansion.

---

## 3. Architectural Strategy: "Layered Data (Delta Pattern)"

```
┌─────────────────────────────────────────────────────────────┐
│                    SINGLE COLLECTION                        │
├─────────────────────────────────────────────────────────────┤
│  Layer 0 (Base):  tenant_id = "ALL"                         │
│  ├── Schemas (Tables, Views)    ← source_type = "schema"    │
│  └── Default Metadata (Rows)    ← source_type = "data"      │
├─────────────────────────────────────────────────────────────┤
│  Layer 1 (Client Override):  tenant_id = "Client_X"         │
│  └── ONLY rows that are DIFFERENT from Layer 0              │
└─────────────────────────────────────────────────────────────┘
```

### Deduplication Keys (From Real Qdrant Data)
| Source Type | Deduplication Key | Example |
|:---|:---|:---|
| `data` | `table_name` + `row_id` | `ListSetups_14041` |
| `schema` | `object_name` | `Employee` |

---

## 4. Deep Code Analysis Summary

### 4.1 Pipeline Architecture (6 Phases)
```
Phase 1: Build Context Map      ← IErpMetadataService.BuildContextMapAsync()
Phase 2: Extract Target Lists   ← Extract view names from context
Phase 3: (Deferred)             ← Skipped for performance
Phase 4: Data Extraction        ← IErpDataExtractorService.ExtractDataChangesAsync() [FULL LOAD]
Phase 5: Schema CDC             ← Uses modify_date comparison [WORKING]
Phase 6: Upload to Qdrant       ← IVectorDbService.SyncBatchAsync()
```

### 4.2 CDC Infrastructure Status

| Component | File | Status | Notes |
|:---|:---|:---|:---|
| `IChangeTrackingStrategy` | Interface | ✅ Ready | Defines CDC contract |
| `CdcChangeStrategy` | Service | ✅ Ready | Uses `cdc.fn_cdc_get_all_changes_*` |
| `CdcChangeResult` | Model | ✅ Ready | Returns changes + new LSN |
| `PipelineState.DataLastLsn` | Model | ✅ Ready | Tracks LSN per table |
| `JsonStateService.GetLastLsnForTable` | Method | ✅ Ready | LSN read/write methods exist |
| **Registration in DI** | `Program.cs:44` | ✅ **REGISTERED** | `services.AddTransient<IChangeTrackingStrategy, CdcChangeStrategy>();` |
| **Injection in ErpDataExtractor** | Constructor | ❌ **NOT INJECTED** | Service doesn't use it! |

**Key Finding:** CDC is 100% built and registered. It's just **not wired** into `ErpDataExtractorService`.

### 4.3 Multi-Tenancy Status

| Component | Current State | Required Change |
|:---|:---|:---|
| `ISchemaOrchestrator.GenerateSchemaAsync()` | No parameters | Add `string tenantId = "ALL"` |
| `ErpDataExtractorService.TransformRowToDocument()` | Hardcoded `TenantAll` | Use passed `tenantId` |
| `VectorDbMeta.TenantAll` | Constant = `"ALL"` | ✅ Already correct |
| `QdrantRetrievalService` | Has tenant filter | Add **Deduplication** logic |

### 4.4 Constants & Metadata Keys
```csharp
// From DbConstants.cs
VectorDbMeta.TenantId = "tenant_id"
VectorDbMeta.SourceType = "source_type"
VectorDbMeta.TableName = "table_name"
VectorDbMeta.RowId = "row_id"
VectorDbMeta.TenantAll = "ALL"
VectorDbMeta.SourceTypeData = "data"
VectorDbMeta.SourceTypeSchema = "schema"
```

---

## 5. Implementation Plan (Revised Modules)

### Module 1: Multi-Tenancy Foundation (MVP)
**Goal:** Enable client-specific data upload and retrieval with deduplication.

| Step | File | Action |
|:---|:---|:---|
| 1.1 | `ISchemaOrchestrator.cs` | Add `string tenantId = "ALL"` parameter |
| 1.2 | `SchemaOrchestrator.cs` | Pass `tenantId` to Phase 4 call |
| 1.3 | `IErpDataExtractorService.cs` | Add `string tenantId` parameter |
| 1.4 | `ErpDataExtractorService.cs` | Use passed `tenantId` in `TransformRowToDocument` metadata |
| 1.5 | `QdrantRetrievalService.cs` | Add **Deduplication Logic** (prefer Client > ALL) |

### Module 2: Activate CDC for Data (Future Optimization)
**Goal:** Use existing CDC infrastructure to upload only changed rows.

| Step | File | Action |
|:---|:---|:---|
| 2.1 | `ErpDataExtractorService.cs` | Inject `IChangeTrackingStrategy` via constructor |
| 2.2 | `ErpDataExtractorService.cs` | Call `GetChangesAsync()` instead of `FetchDataAsync()` |
| 2.3 | `ErpDataExtractorService.cs` | Handle CDC operations (INSERT/UPDATE/DELETE) |
| 2.4 | `SchemaOrchestrator.cs` | Save LSN after successful upload |

**Note:** Module 2 requires CDC to be enabled on SQL Server tables (`sys.sp_cdc_enable_table`).

---

## 6. Migration Strategy (Cloud → Local)

| Step | Action |
|:---|:---|
| 1 | Trigger Snapshot via Qdrant Cloud Console or API. |
| 2 | Download `.snapshot` file. |
| 3 | Restore to local Docker (`qdrant/qdrant`) using `/collections/{name}/snapshots/upload`. |

---

## 7. Verification Plan

### 7.1 Module 1 Test Scenario

| Step | Action | Expected Result |
|:---|:---|:---|
| 1 | Run Bootstrapper (default: `tenantId = "ALL"`). | Data uploaded with `tenant_id = "ALL"`. |
| 2 | Modify Row 14041's `arColumnLabel` in SQL DB. | - |
| 3 | Run Bootstrapper with `tenantId = "Client_A"`. | Changed row uploaded with `tenant_id = "Client_A"`. |
| 4 | Query as "Client_A" about Row 14041. | System returns **Client_A's version** (deduplicated). |
| 5 | Query as "Client_B" about Row 14041. | System returns **ALL version** (fallback). |

### 7.2 Build Verification
```bash
dotnet build DynamicAi.sln
dotnet test DynamicAI.Tests
```

---

## 8. Files Affected Summary

### Module 1 (Multi-Tenancy)
| File | Change Type | Complexity |
|:---|:---|:---|
| `SchemaGenerator/Interfaces/ISchemaOrchestrator.cs` | Modify | Low |
| `SchemaGenerator/SchemaOrchestrator.cs` | Modify | Low |
| `SchemaGenerator/Interfaces/IErpDataExtractorService.cs` | Modify | Low |
| `SchemaGenerator/Services/ErpDataExtractorService.cs` | Modify | Low |
| `DynamicAi.Application/Services/QdrantRetrievalService.cs` | Modify | Medium |

### Module 2 (CDC Activation)
| File | Change Type | Complexity |
|:---|:---|:---|
| `SchemaGenerator/Services/ErpDataExtractorService.cs` | Major Refactor | High |
| `SchemaGenerator/SchemaOrchestrator.cs` | Modify | Medium |

---

## 9. Appendix: Deduplication Pseudocode

```csharp
// In QdrantRetrievalService after search
private List<string> DeduplicateResults(List<ScoredPoint> results, string clientTenantId)
{
    return results
        .GroupBy(p => GetDeduplicationKey(p))
        .Select(group => 
        {
            // If there's a client-specific version, prefer it over "ALL"
            var clientVersion = group.FirstOrDefault(p => 
                p.Payload["tenant_id"].StringValue == clientTenantId);
            return clientVersion ?? group.First(); // Fallback to "ALL"
        })
        .Select(p => p.Payload["original_text"].StringValue)
        .ToList();
}

private string GetDeduplicationKey(ScoredPoint point)
{
    var sourceType = point.Payload["source_type"].StringValue;
    if (sourceType == "schema")
        return point.Payload["object_name"].StringValue;
    else // "data"
        return $"{point.Payload["table_name"].StringValue}_{point.Payload["row_id"].StringValue}";
}
```

---

## 10. Key Discoveries Summary

| Finding | Impact |
|:---|:---|
| CDC is **built and registered** but **not wired** | Module 2 is mostly "activation", not development |
| `tenant_id = "ALL"` already exists in constants | No migration needed for existing data |
| LSN tracking infrastructure exists in `IStateService` | Ready for CDC integration |
| Schema CDC (via `modify_date`) is **working** | Only data CDC needs activation |
| 6-phase checkpointed pipeline | Robust, resumable process |

---

**الخطة جاهزة. هل توافق على البدء بتنفيذ Module 1؟**
