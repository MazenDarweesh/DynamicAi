# [Roadmap V4.0: Qdrant Scaling & Multi-Tenancy Architecture]
> **Last Updated:** 2024-12-24 | **Status:** Executing Module 0

---

## 1. Goals & Objectives

| Goal | Description |
|:---|:---|
| **Dynamic Page Discovery** | اكتشاف الصفحات المفعّلة من الداتابيز (AISetup = 1) |
| **توفير المساحة** | تخزين الفرق فقط (Delta)، وليس نسخة كاملة لكل عميل |
| **تسريع التسطيب** | عميل جديد = رفع التغييرات فقط (إن وجدت) |
| **عدم التكرار** | الـ Schema والـ Metadata المشتركة تُخزن مرة واحدة |

---

## 2. Implementation Plan (Prioritized Modules)

### ✅ Module 0: Dynamic Page Discovery (Current)
**Goal:** Replace hardcoded `_targetPageIds` with database-driven discovery via `AISetup` column.

| Step | File | Action | Status |
|:---|:---|:---|:---:|
| 0.1 | `IErpMetadataService.cs` | Add `GetAIEnabledPageIdsAsync()` method signature | 🔄 |
| 0.2 | `ErpMetadataService.cs` | Implement `SELECT PageID FROM Pages WHERE AISetup = 1` | 🔄 |
| 0.3 | `SchemaOrchestrator.cs` | Replace `_targetPageIds` with dynamic call | 🔄 |
| 0.4 | Build & Test | `dotnet build` + verify correct pages loaded | ⏳ |

**Before:**
```csharp
private readonly List<int> _targetPageIds = new List<int> { 244, 289 };
```

**After:**
```csharp
var targetPageIds = await _metadataService.GetAIEnabledPageIdsAsync(_connectionString, _dbName);
```

---

### ⏳ Module 1: Multi-Tenancy Foundation (Deferred)
**Goal:** Enable client-specific data upload and retrieval with deduplication.
**Status:** Deferred until clients are onboarded.

| Step | File | Action |
|:---|:---|:---|
| 1.1 | `ISchemaOrchestrator.cs` | Add `string tenantId = "ALL"` parameter |
| 1.2 | `SchemaOrchestrator.cs` | Pass `tenantId` to Phase 4 call |
| 1.3 | `IErpDataExtractorService.cs` | Add `string tenantId` parameter |
| 1.4 | `ErpDataExtractorService.cs` | Use passed `tenantId` in metadata |
| 1.5 | `QdrantRetrievalService.cs` | Add Deduplication Logic |

---

### ⏳ Module 2: CDC Activation (Future)
**Goal:** Use existing CDC infrastructure to upload only changed rows.

| Step | File | Action |
|:---|:---|:---|
| 2.1 | `ErpDataExtractorService.cs` | Inject `IChangeTrackingStrategy` |
| 2.2 | `ErpDataExtractorService.cs` | Call `GetChangesAsync()` instead of Full Load |
| 2.3 | SQL Server | Enable CDC on tables (`sys.sp_cdc_enable_table`) |

---

## 3. Architecture Diagram

```text
┌─────────────────────────────────────────────────────────────────┐
│                    INGESTION PIPELINE                            │
├──────────┬──────────────────────────────────────────────────────┤
│ NEW!     │ GetAIEnabledPageIdsAsync()                           │
│          │ → SELECT PageID FROM Pages WHERE AISetup = 1         │
├──────────┼──────────────────────────────────────────────────────┤
│ Phase 1  │ Build Context Map (using dynamic page IDs)           │
├──────────┼──────────────────────────────────────────────────────┤
│ Phase 2  │ Extract Target Lists                                 │
├──────────┼──────────────────────────────────────────────────────┤
│ Phase 3  │ (Deferred)                                           │
├──────────┼──────────────────────────────────────────────────────┤
│ Phase 4  │ Data Extraction (Full Load)                          │
├──────────┼──────────────────────────────────────────────────────┤
│ Phase 5  │ Schema CDC (modify_date comparison)                  │
├──────────┼──────────────────────────────────────────────────────┤
│ Phase 6  │ Upload to Qdrant                                     │
└──────────┴──────────────────────────────────────────────────────┘
```

---

## 4. Verification Plan

### Module 0 Test
```sql
-- Verify AISetup column exists and has correct values:
SELECT PageID, AISetup FROM Pages WHERE AISetup = 1
-- Expected: 244, 289 (at minimum)
```

```bash
# Build verification:
dotnet build SchemaGenerator/SchemaGenerator.csproj

# Run and verify output:
cd SchemaBootstrapper
dotnet run
# Expected: "[Phase 1] Found X AI-enabled pages from database."
```

---

## 5. Key Discoveries (From Code Analysis)

| Finding | Impact |
|:---|:---|
| CDC infrastructure **built and registered** but **not wired** | Module 2 is mostly "activation" |
| `tenant_id = "ALL"` already exists in constants | No migration needed |
| LSN tracking infrastructure exists in `IStateService` | Ready for CDC |
| Schema CDC (via `modify_date`) is **working** | Only data CDC needs activation |
| 6-phase checkpointed pipeline | Robust, resumable process |
