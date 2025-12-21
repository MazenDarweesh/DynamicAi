# الملف الأول: QueryOrchestrator.cs (المايسترو)

> **"هذا الملف هو المدير التنفيذي للنظام. لا يقوم بالعمل بيده، ولكنه يضمن أن الجميع يعمل بتناغم."**

---

## 1. الكود الكامل (The Full Code)

```csharp
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// التنفيذ الفعلي لـ IQueryOrchestrator.
    /// يقوم بربط جميع الخدمات (Layers 1-5) معاً بالترتيب الصحيح.
    /// </summary>
    public class QueryOrchestrator : IQueryOrchestrator
    {
        private readonly IRagRetrievalService _retrievalService;         // (Layer 1)
        private readonly ISqlGenerationService _generationService;       // (Layer 2)
        private readonly ISqlValidatorService _validatorService;         // (Layer 3)
        private readonly ISqlRewriterService _rewriterService;           // (Layer 4)
        private readonly ISandboxedSqlExecutor _executorService;         // (Layer 5)
        private readonly IQueryCacheService _queryCacheService;          // (Cache Layer)
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
        private readonly bool _queryCacheEnabled;

        public QueryOrchestrator(
            IRagRetrievalService retrievalService,
            ISqlGenerationService generationService,
            ISqlValidatorService validatorService,
            ISqlRewriterService rewriterService,
            ISandboxedSqlExecutor executorService,
            IQueryCacheService queryCacheService,
            IEmbeddingGenerator<string, Embedding<float>> embeddingService,
            IConfiguration configuration)
        {
            _retrievalService = retrievalService;
            _generationService = generationService;
            _validatorService = validatorService;
            _rewriterService = rewriterService;
            _executorService = executorService;
            _queryCacheService = queryCacheService;
            _embeddingService = embeddingService;
            _queryCacheEnabled = bool.Parse(configuration["Caching:QueryCache:Enabled"] ?? "true");
        }

        public async Task<QueryExecutionResult> HandleQueryAsync(string userQuestion, string tenantId, int? comID = null)
        {
            // ... (Logging Logic Omitted for Brevity) ...

            // Layer 0: Cache Check
            if (_queryCacheEnabled)
            {
                 // Logic to check cache...
            }

            // Layer 1: RAG Retrieval
            var contextSnippets = await _retrievalService.RetrieveContextAsync(userQuestion, tenantId);

            // Layer 2: SQL Generation
            string rawSql = await _generationService.GenerateSqlAsync(userQuestion, contextSnippets);

            // Layer 3: Validation
            await _validatorService.ValidateSqlAsync(rawSql, tenantId);

            // Layer 4: Rewriting & Hardening
            var rewrittenResult = await _rewriterService.RewriteAsync(rawSql, tenantId, comID);

            // Layer 5: Execution
            var data = await _executorService.ExecuteSqlAsync(rewrittenResult.RewrittenSql, rewrittenResult.Parameters);

            return new QueryExecutionResult
                {
                    Data = data,
                    RewrittenSql = rewrittenResult?.RewrittenSql ?? rawSql,
                    IsCached = isCached,
                    CacheId = cacheId
                };
        }
    }
}
```

---

## 2. أهم الأجزاء المرتبطة (Key Dependencies)

هذا الملف لا يعمل وحيداً، بل يعتمد على `Interfaces` محددة تجعل النظام قابلاً للاختبار والتغيير:

### `IQueryExecutionResult` (النتيجة النهائية)
هذا هو الصندوق الذي يسلمه الـ Orchestrator للـ Controller في النهاية ويحتوي على:
1.  **Data:** البيانات القادمة من الداتابيز (جدول النتائج).
2.  **RewrittenSql:** كود الـ SQL الذي تم تنفيذه (للعرض بشفافية للمستخدم).
3.  **IsCached:** علامة تخبرنا هل جاءت الإجابة من الذاكرة أم تم توليدها الآن.

---

## 3. الشرح والتحليل (Deep Dive Analysis)

### دوره في المشروع (Role)
الـ `QueryOrchestrator` هو نقطة الدخول الوحيدة لكل عملية "ذكية". هو الذي يقرر الترتيب:
1.  **أولاً:** اسأل الكاش (وفر فلوس ووقت).
2.  **ثانياً:** إذا لم تجد، اسأل RAG (هات الجداول).
3.  **ثالثاً:** اسأل Gemini (اكتب الكود).
4.  **رابعاً:** راجع الكود (Validation).
5.  **خامساً:** نفذ الكود.

### تصميمه (Design Pattern)
يستخدم نمط **Facade / Mediator**، حيث يخفي تعقيدات الـ Subsystems (مثل Gemini و Qdrant و SQL) خلف واجهة واحدة بسيطة اسمها `HandleQueryAsync`.

---

## 4. مثال حقيقي (Real-World Example)

**المستخدم يسأل:** *"أعطني قائمة بأغلى 5 منتجات"*

1.  **Orchestrator:** يستلم السؤال.
2.  **Layer 0 (Cache):** يسأل "هل سأل أحد هذا من قبل؟" -> الإجابة: لا.
3.  **Layer 1 (RAG):** يذهب لـ Qdrant. يجد أن جدول `Products` وجدول `Prices` هم الأهم. يعيد تعريفاتهم.
4.  **Layer 2 (Gen):** يرسل السؤال + جدول `Products` لـ Gemini.
    *   Gemini يرد: `SELECT TOP 5 Name, Price FROM Products ORDER BY Price DESC`
5.  **Layer 3 (Val):** يتأكد أن الكود لا يحتوي على `DROP` أو `DELETE`.
6.  **Layer 5 (Exec):** ينفذ الكود ويعيد قائمة المنتجات.

**النتيجة:** المستخدم يرى جدولاً، ولا يعلم أن هناك 5 عمليات معقدة حدثت في الخلفية في أقل من ثانيتين.
