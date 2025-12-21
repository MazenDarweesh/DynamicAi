# الملف الرابع: QueryCacheService.cs (الذاكرة طويلة الأمد)

> **"لماذا نخترع العجلة في كل مرة؟ إذا سأل أحد نفس السؤال سابقاً، فلنستخدم إجابته الجاهزة."**

---

## 1. الكود الكامل ومقتطفات هامة (The Core Logic)

هذا الملف لا يستخدم `Dictionary` عادي للـ Caching، بل يستخدم **Semantic Caching** (كاش دلالي) باستخدام Qdrant.

```csharp
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Qdrant.Client;

namespace DynamicAi.Application.Services
{
    public class QueryCacheService : IQueryCacheService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly float _similarityThreshold = 0.92f; // نسبة تشابه عالية جداً

        public async Task<CachedQueryResult?> TryGetCachedSqlAsync(
            float[] questionEmbedding, string tenantId, int companyId)
        {
            // 1. البحث في Qdrant عن سؤال مشابه (Vector Search)
            var searchResult = await _qdrantClient.SearchAsync(
                _collectionName,
                questionEmbedding,
                filter: CreateSecurityFilter(tenantId, companyId), // فلتر أمان إجباري
                scoreThreshold: _similarityThreshold // (0.92)
            );

            // 2. إذا وجدنا نتيجة
            if (searchResult.Any())
            {
                var payload = searchResult.First().Payload;
                return new CachedQueryResult 
                { 
                    Sql = payload["generated_sql"], 
                    Score = searchResult.First().Score 
                };
            }

            return null; // Cache Miss
        }
    }
}
```

---

## 2. ما هو Semantic Caching؟

الفرق بين الكاش العادي والكاش الذكي:
*   **الكاش العادي (Exact Match):**
    *   س: "مبيعات اليوم" -> إجابة 1.
    *   س: "مبيعات النهارده" -> لا يعرفها (Cache Miss).
*   **الكاش الذكي (Semantic Match):**
    *   س: "مبيعات اليوم" -> إجابة 1.
    *   س: "مبيعات النهارده" -> يفهم أنها نفس المعنى ( similarity 0.98) -> يعيد إجابة 1 فوراً.
    *   **النتيجة:** توفير 3 ثواني + 0 تكلفة لـ LLM.

---

## 3. الشرح والتحليل (Deep Dive Analysis)

### دوره في المشروع (Role)
هو خط الدفاع الأول. قبل أن نذهب لـ RAG أو LLM، نسأل الكاش. إذا أجاب، تنتهي الرحلة هنا (Layer 0).

### الأمان (Security)
الكاش ليس عاماً للجميع. لا يجوز لشركة A أن ترى كاش شركة B.
*   **Multi-Tenant Isolation:** كل عملية بحث في الكاش مفلترة بـ `tenant_id` و `company_id`.

---

## 4. مثال حقيقي (Real-World Example)

**المستخدم A يسأل:** *"قائمة العملاء في القاهرة"*
*   النظام يولد SQL ويخزنه في الكاش.

**المستخدم B (من نفس الشركة) يسأل:** *"العملاء الي ساكنين في القاهرة"*
1.  **Orchestrator:** يرسل السؤال للكاش.
2.  **QueryCacheService:**
    *   يحول السؤال لـ Vector.
    *   يقارنه بالكاش. يجد تشابهاً بنسبة 95% مع سؤال المستخدم A.
3.  **Result:** يعيد الـ SQL الجاهز فوراً: `SELECT * FROM Customers WHERE City = 'Cairo'`.
4.  **الوقت المستغرق:** 15 مللي ثانية (بدلاً من 2500 مللي ثانية).
