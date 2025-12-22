# الملف الثالث: QdrantRetrievalService.cs (أمين المخزن الذكي)

> **"لا يعطينا كل البيانات، بل يعطينا فقط ما نحتاجه للإجابة على السؤال. يبحث عن الإبرة في كومة القش."**

---

## 1. الكود الكامل ومقتطفات هامة (The Core Logic)

هذا الملف ينفذ خوارزمية بحث معقدة (Multi-Step Retrieval) لضمان أننا نجمع السياق الصحيح من بين آلاف الجداول.

```csharp
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Qdrant.Client;

namespace DynamicAi.Application.Services
{
    public class QdrantRetrievalService : IRagRetrievalService
    {
        private readonly QdrantClient _qdrantClient;

        public async Task<RetrievalContextBundle> RetrieveContextAsync(string userQuestion, string tenantId)
        {
            var contextBundle = new RetrievalContextBundle();

            // 1. تحويل السؤال إلى Vector
            var embedding = await _embeddingService.GenerateAsync(userQuestion);

            // 2. البحث عن الصفحة (Page Discovery)
            // نبحث أولاً عن "أقرب صفحة" في النظام تتحدث عن موضوع السؤال.
            // مثلاً: "مبيعات" -> Page: SalesInvoice
            var pageResults = await SearchQdrantAsync(embedding, "Pages", limit: 1);
            var pageId = ExtractPageId(pageResults.First());

            // 3. البحث عن الأعمدة (Attribute Discovery)
            // نبحث عن الحقول المرتبطة بهذه الصفحة والتي ذكرها المستخدم.
            var attrResults = await SearchQdrantAsync(embedding, "PageAttribute", limit: 20, filterByPage: pageId);

            // 4. بناء القاموس (Dictionary Construction)
            foreach (var attr in attrResults)
            {
                // إذا كان الحقل "dropdown"، نذهب لجدول الـ Lookup ونجلب تعريفاته
                if (attr.HasListName)
                {
                     var listEntries = await SearchQdrantAsync(embedding, "ListSetups", filterByList: attr.ListName);
                     contextBundle.DictionaryEntries.AddRange(listEntries);
                }
            }

            // 5. تجميع الكل في "Bundle" واحدة
            return contextBundle;
        }
    }
}
```

---

## 2. استراتيجية البحث (Search Strategy)

لا نقوم ببحث واحد بسيط، بل نتتبع الخيوط:
1.  **Step 1: Identify Topic (Page Level):** أين نحن؟ (مبيعات، مخازن، موظفين؟).
2.  **Step 2: Identify Details (Attribute Level):** ماذا يريد المستخدم بالضبط؟ (تاريخ، كمية، اسم؟).
3.  **Step 3: Resolve Definitions (Dictionary Level):** ما معنى "النوع" في هذا السياق؟ (هل هو `ItemType` أم `CustomerType`؟).

### ميزة التشخيص الذاتي (Self-Healing)
إذا لم يجد نتائج باستخدام البحث الدلالي (Semantic Search)، يبحث عن **Explicit IDs** (مثل `PageId 55`) إذا كان المطور يقوم بالتجربة، مما يسهل عملية الـ Debugging.

---

## 3. الشرح والتحليل (Deep Dive Analysis)

### دوره في المشروع (Role)
يقلل حجم البيانات المرسلة للـ LLM (Token Saving). بدلاً من إرسال قاعدة البيانات بالكامل (وهو مستحيل)، يرسل فقط الـ 5% المتعلقة بالسؤال.

### التحدي والحل
*   **التحدي:** تشابه الأسماء (كلمة "تاريخ" موجودة في 500 جدول).
*   **الحل:** البحث الهرمي. نحدد الصفحة أولاً، ثم نبحث عن "تاريخ" *داخل* هذه الصفحة فقط، مما يزيل الغموض.

---

## 4. مثال حقيقي (Real-World Example)

**المستخدم يسأل:** *"فواتير العميل أحمد"*

1.  **Step 1 (Page):** البحث عن "فواتير".
    *   Qdrant يجد: `Page: SalesInvoice` (Score: 0.92).
2.  **Step 2 (Attributes):** البحث عن "عميل" و "أحمد" داخل `SalesInvoice`.
    *   Qdrant يجد: `Attribute: CustomerName` (Link to `CustomerView`).
3.  **Step 3 (Dictionary):** البحث عن "أحمد" في قائمة العملاء.
    *   Qdrant لا يجد "أحمد" لأننا لا نخزن البيانات، بل نخزن *وصف* البيانات. لكنه يجلب تعريف أن `CustomerName` هو حقل نصي للبحث.
4.  **Result:** الـ Bundle تحتوي على:
    *   `CREATE VIEW SalesInvoice ...`
    *   `CustomerName | dropdown | CustomerView`
