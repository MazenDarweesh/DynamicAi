# الملف الخامس: SchemaOrchestrator.cs (المهندس المعماري)

> **"كيف يعرف الذكاء الاصطناعي كل شيء عن قاعدة البيانات؟ لأن هذا الملف قام بقراءتها، فهمها، وإرسالها إليه."**

---

## 1. الكود الكامل ومقتطفات هامة (The Core Logic)

هذا الملف هو المسؤول عن **Ingestion Pipeline** (خط تلقيم البيانات). يعمل بشكل منفصل عن الموقع (Offline Process) لتجهيز المعلومات.

```csharp
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;

namespace SchemaGenerator
{
    public class SchemaOrchestrator : ISchemaOrchestrator
    {
        // قائمة الصفحات التي نريد تعليم الذكاء الاصطناعي عنها
        private readonly List<int> _targetPageIds = new List<int> { 244, 289 };

        public async Task GenerateSchemaAsync()
        {
            // نظام Checkpoint: إذا فشلت العملية في المنتصف، نكمل من حيث توقفنا
            var checkpoint = await LoadCheckpointAsync();

            // Phase 1: Build Context Map
            // نفهم العلاقات بين الجداول (Master-Detail) من الداتابيز
            if (checkpoint.LastCompletedPhase < 1) {
                 checkpoint.ContextMap = await _metadataService.BuildContextMapAsync(...);
                 await SaveCheckpointAsync(checkpoint);
            }

            // Phase 5: Check State (Smart CDC)
            // لا نرفع كل شيء كل مرة. نفحص تاريخ التعديل (modify_date)
            if (checkpoint.LastCompletedPhase < 5) {
                 var changes = DetectChanges(checkpoint.TargetViewNames);
                 if (changes.Any()) {
                     // فقط نرفع الجداول التي تغير هيكلها
                     await _vectorDbService.SyncBatchAsync(_collectionName, changes);
                 }
            }
        }
    }
}
```

---

## 2. مراحل البناء (The 6 Phases)

يقوم النظام ببناء المعرفة في 6 مراحل متتالية:
1.  **Phase 1 (Context Map):** قراءة الـ Metadata من جداول `PageAttribute` و `PageDetail` لفهم الهيكل.
2.  **Phase 2 (Scan):** تحديد قوائم الجداول المطلوب مراقبتها.
3.  **Phase 3 (Schema Gen):** استخراج كود `CREATE VIEW` لكل جدول.
4.  **Phase 4 (Data Extractor):** استخراج عينات من البيانات (أول 10 صفوف) لفهم المحتوى.
5.  **Phase 5 (Change Detection):** مقارنة التواريخ لتحديد ما هو جديد.
6.  **Phase 6 (Upload):** الإرسال إلى Qdrant.

---

## 3. الشرح والتحليل (Deep Dive Analysis)

### دوره في المشروع (Role)
هو "المعلم" الذي يغذي "دماغ" الذكاء الاصطناعي. بدونه، يكون الـ LLM جاهلاً ببيانات الشركة.

### الذكاء في التصميم (Design Intelligence)
*   **Resumable:** لو انقطع الإنترنت في المرحلة 4، عند التشغيل مرة أخرى يبدأ من 4 (وليس من الصفر) بفضل ملف `pipeline_checkpoint.json`.
*   **Incremental Updates:** لو عندك 1000 جدول، وتغير جدول واحد، يقوم برفع الجدول الواحد فقط. هذا يوفر ساعات من المعالجة.

---

## 4. مثال حقيقي (Real-World Example)

**المطور أضاف عمود جديد "TaxAmount" في الداتابيز.**

1.  **SchemaOrchestrator:** يعمل.
2.  **Phase 5:** يلاحظ أن `sys.objects.modify_date` لجدول الفواتير تغير اليوم.
3.  **Action:**
    *   يقوم بتوليد `CREATE VIEW` الجديد (الذي يحتوي على `TaxAmount`).
    *   يمسح النسخة القديمة من Qdrant.
    *   يرفع النسخة الجديدة.
4.  **النتيجة:** بعد دقيقة واحدة، إذا سأل المستخدم "كم قيمة الضريبة؟"، سيفهم النظام السؤال فوراً.
