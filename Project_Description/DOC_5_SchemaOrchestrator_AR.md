# الملف الخامس: SchemaOrchestrator.cs (العقل المدبر - The Brain)

> **"كيف يعرف الذكاء الاصطناعي كل شيء عن قاعدة البيانات؟ لأن هذا الملف قام بقراءتها، فهمها، وإرسالها إليه."**

---

## 1. نظرة عامة (Overview)

هذا الملف هو **"العقل المدبر"** (The Orchestrator) لخط أنابيب تلقيم المعرفة (Knowledge Ingestion Pipeline). يعمل بشكل منفصل عن الموقع (Offline Process) لتجهيز المعلومات.

**الموقع:** `SchemaGenerator/SchemaOrchestrator.cs` (350 سطر)

**المسؤوليات:**
1. تنسيق جميع الخدمات الخمس في البنية التحتية.
2. إدارة نظام Checkpoint للاستئناف بعد الأعطال.
3. تتبع التغييرات (CDC) للسكيما والبيانات.
4. رفع المستندات إلى Qdrant.

---

## 2. بنية الخدمات (Service Architecture)

```csharp
public class SchemaOrchestrator : ISchemaOrchestrator
{
    // ← الخدمات الخمس المعتمدة
    private readonly IStateService _stateService;           // إدارة الحالة
    private readonly IErpMetadataService _metadataService;  // قراءة الـ Context
    private readonly ISchemaReaderService _schemaReaderService; // استخراج SMO
    private readonly IErpDataExtractorService _dataExtractorService; // استخراج البيانات
    private readonly IVectorDbService _vectorDbService;     // الرفع إلى Qdrant
    
    // ← الصفحات المستهدفة (قابلة للتوسيع)
    private readonly List<int> _targetPageIds = new List<int> { 244, 289 };
}
```

---

## 3. مراحل الـ Pipeline (The 6 Phases)

```text
┌─────────────────────────────────────────────────────────────────┐
│                    INGESTION PIPELINE                            │
├──────────┬──────────────────────────────────────────────────────┤
│ Phase 1  │ Build Context Map (IErpMetadataService)              │
│          │ → قراءة Pages, PageAttribute, PageDetail             │
├──────────┼──────────────────────────────────────────────────────┤
│ Phase 2  │ Extract Target Lists                                 │
│          │ → تحديد أسماء الـ Views والجداول المطلوبة             │
├──────────┼──────────────────────────────────────────────────────┤
│ Phase 3  │ (Deferred) - مؤجل للأداء                              │
├──────────┼──────────────────────────────────────────────────────┤
│ Phase 4  │ Data Extraction (IErpDataExtractorService)           │
│          │ → استخراج الـ Metadata (Full Load حالياً)             │
├──────────┼──────────────────────────────────────────────────────┤
│ Phase 5  │ Schema CDC (modify_date comparison)                  │
│          │ → فقط الـ Views/Tables التي تغيرت                     │
├──────────┼──────────────────────────────────────────────────────┤
│ Phase 6  │ Upload to Qdrant (IVectorDbService)                  │
│          │ → مزامنة المستندات مع حفظ الحالة                     │
└──────────┴──────────────────────────────────────────────────────┘
```

### تفاصيل كل مرحلة:

| المرحلة | الوصف | الإخراج |
|:---|:---|:---|
| **Phase 1** | قراءة الـ Metadata من `Pages`, `PageAttribute`, `PageDetail` | `ContextMap` |
| **Phase 2** | استخراج قوائم الـ Views والجداول | `TargetViewNames`, `TargetMetadataTables` |
| **Phase 3** | *(مؤجلة)* | - |
| **Phase 4** | استخراج صفوف الـ Metadata | `DataDocuments` |
| **Phase 5** | مقارنة `sys.objects.modify_date` لاكتشاف التغييرات | `DocumentsToUpload` |
| **Phase 6** | رفع إلى Qdrant + حفظ الحالة | ✅ Upload Complete |

---

## 4. نظام الـ Checkpoint (Resiliency)

```csharp
// إذا فشلت العملية، نكمل من حيث توقفنا
var checkpoint = await LoadCheckpointAsync();

if (checkpoint.LastCompletedPhase < 5)
{
    // ... تنفيذ المرحلة 5
    checkpoint.LastCompletedPhase = 5;
    await SaveCheckpointAsync(checkpoint);  // ← حفظ التقدم
}
```

**الملفات المستخدمة:**
- `pipeline_checkpoint.json` - تتبع المراحل المكتملة
- `process_state.json` - تتبع تواريخ التعديل وLSN

---

## 5. بنية CDC التحتية (موجودة لكن غير مفعلة)

> **اكتشاف مهم:** الـ CDC جاهز 100% لكنه **غير موصول**!

| المكون | الملف | الحالة |
|:---|:---|:---|
| `IChangeTrackingStrategy` | Interface | ✅ جاهز |
| `CdcChangeStrategy` | Service | ✅ جاهز (يستخدم `cdc.fn_cdc_get_all_changes_*`) |
| `CdcChangeResult` | Model | ✅ جاهز |
| **التسجيل في DI** | `Program.cs:44` | ✅ **مسجل** |
| **الحقن في DataExtractor** | Constructor | ❌ **غير محقون!** |

**المطلوب للتفعيل:**
```csharp
// في ErpDataExtractorService.cs
public ErpDataExtractorService(
    IStateService stateService,
    IChangeTrackingStrategy cdcStrategy  // ← إضافة هذا
)
```

---

## 6. دعم Multi-Tenancy (قيد التطوير)

**الحالة الحالية:**
- الـ `tenant_id` مثبت على `"ALL"` لجميع البيانات.
- لا يوجد parameter لتمرير الـ tenant.

**الخطة المستقبلية:**
```csharp
// الواجهة الحالية
Task GenerateSchemaAsync();

// الواجهة المطلوبة (Module 1)
Task GenerateSchemaAsync(string tenantId = "ALL");
```

---

## 7. مثال حقيقي (Real-World Example)

### السيناريو: المطور أضاف عمود جديد "TaxAmount"

```text
1. [SchemaOrchestrator] → يبدأ
2. [Phase 1-4] → ✓ تم سابقاً (Checkpoint)
3. [Phase 5] → يقارن sys.objects.modify_date
   → يكتشف: "InvoiceView" تغير اليوم!
4. [SchemaReaderService] → يولد CREATE VIEW الجديد
5. [QdrantVectorDbService] → يحذف القديم، يرفع الجديد
6. [Phase 6] → ✅ Complete

⏱️ النتيجة: بعد دقيقة، المستخدم يسأل "كم الضريبة؟"
            → النظام يفهم السؤال فوراً!
```

---

## 8. الكود الأساسي (Core Logic Snippet)

```csharp
public async Task GenerateSchemaAsync()
{
    var checkpoint = await LoadCheckpointAsync();
    
    // Phase 1: Build Context Map
    if (checkpoint.LastCompletedPhase < 1)
    {
        checkpoint.ContextMap = await _metadataService.BuildContextMapAsync(
            _connectionString, _dbName, _targetPageIds);
        await SaveCheckpointAsync(checkpoint);
    }
    
    // Phase 5: Smart CDC Detection
    if (checkpoint.LastCompletedPhase < 5)
    {
        var state = await _stateService.LoadStateAsync(_stateFilePath);
        var currentDates = await _schemaReaderService.GetObjectsModificationDatesAsync(
            _connectionString, normalizedTargetViewNames);
        
        foreach (var objName in normalizedTargetViewNames)
        {
            if (currentDates.TryGetValue(objName, out var currentDate))
            {
                if (!state.SchemaDates.TryGetValue(objName, out var storedDate) 
                    || currentDate > storedDate)
                {
                    changedObjectNames.Add(objName);  // ← تم اكتشاف تغيير!
                }
            }
        }
        // ... generate and upload changed schemas
    }
}
```

---

## 9. ملخص الميزات

| الميزة | الحالة | الوصف |
|:---|:---:|:---|
| **6-Phase Pipeline** | ✅ | خط أنابيب مقسم لمراحل واضحة |
| **Checkpoint Resiliency** | ✅ | استئناف من نقطة الفشل |
| **Schema CDC** | ✅ | تتبع التغييرات عبر `modify_date` |
| **Data CDC** | ⚠️ | مُعد لكن غير موصول |
| **Multi-Tenancy** | ⚠️ | مخطط (Module 1) |
| **Rate Limiting** | ✅ | Tier 1 Gemini optimization |
