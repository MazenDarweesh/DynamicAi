# الملف الثاني: SqlGenerationService.cs (المترجم الذكي)

> **"هذا الملف هو المسؤول عن تحويل لغة البشر (العربية/الإنجليزية) إلى لغة الآلة (SQL). إنه القلب النابض للذكاء الاصطناعي في النظام."**

---

## 1. الكود الكامل ومقتطفات هامة (The Core Logic)

سنركز هنا على الأجزاء التي تجعل هذا الـ Service مميزاً، خاصةً الـ **Prompt Engineering** والـ **Caching**.

```csharp
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Text;

namespace DynamicAi.Application.Services
{
    public class SqlGenerationService : ISqlGenerationService
    {
        private readonly IChatCompletionService _chatService;
        
        // ⚡ Gemini Implicit Cache Optimization
        // نقوم بتخزين "القواعد الثابتة" في متغير Static.
        // السبب: عندما يرى Gemini نفس بداية الـ Prompt في كل طلب، يقوم بعمل Cache لها تلقائياً.
        // النتيجة: خصم 75% على التكلفة + سرعة استجابة أعلى.
        private static readonly Lazy<string> _cachedStaticRules = new Lazy<string>(() => BuildStaticSystemRules());

        public SqlGenerationService(IChatCompletionService chatService)
        {
            _chatService = chatService;
        }

        public async Task<string> GenerateSqlAsync(string userQuestion, RetrievalContextBundle contextBundle)
        {
            var promptBuilder = new StringBuilder();

            // 1. حقن القواعد الثابتة (أول 3000 Token)
            promptBuilder.Append(_cachedStaticRules.Value);

            // 2. حقن السياق المتغير (Dynamic Context)
            promptBuilder.AppendLine("--- SCHEMA CONTEXT ---");
            promptBuilder.AppendLine(contextBundle.UnifiedPageContext); // الـ CREATE TABLE

            promptBuilder.AppendLine("--- DICTIONARY ---");
            // تحويل القاموس إلى تنسيق مضغوط (Compact Format) لتوفير التوكيز
            foreach (var entry in contextBundle.DictionaryEntries)
            {
                 promptBuilder.AppendLine(entry); // e.g. "Gov | GovName | dropdown | الحي"
            }

            // 3. حقن السؤال
            promptBuilder.AppendLine($"Q: {userQuestion}");
            promptBuilder.AppendLine("A: ");

            // 4. إرسال الطلب لـ Gemini Flash
            var response = await _chatService.GetChatMessageContentAsync(promptBuilder.ToString());
            
            // 5. استخراج الـ SQL وتنظيفه
            return ExtractSql(response.Content);
        }

        // هذا هو "الدستور" الذي يتبعه الذكاء الاصطناعي
        private static string BuildStaticSystemRules()
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are an expert ERP Database Architect...");
            sb.AppendLine("--- CORE RULES ---");
            sb.AppendLine("1. SELECT ONLY: No INSERT, UPDATE, DELETE.");
            sb.AppendLine("2. NO HALLUCINATION: Only use tables provided in Context.");
            sb.AppendLine("3. MANDATORY FILTERS: Always apply 'ComID = @CurrentCompanyId'.");
            return sb.ToString();
        }
    }
}
```

---

## 2. أهم الأجزاء المرتبطة (Key Concepts)

### `RetrievalContextBundle` (الحقيبة)
هذه الـ Class (موديل) تحتوي على كل ما يحتاجه الـ LLM ليفهم قاعدة البيانات دون أن يرى الداتابيز كاملة:
1.  **UnifiedPageContext:** هيكل الجدول الرئيسي (Master) وتفاصيله (Details).
2.  **DictionaryEntries:** "القاموس" الذي يترجم "الحي" إلى `GovName` و "الصنف" إلى `ItemName`.
3.  **BusinessRules:** الفلاتر الإجبارية (مثلاً: لا تظهر الفواتير الملغاة).

---

## 3. الشرح والتحليل (Deep Dive Analysis)

### دوره في المشروع (Role)
هذا الملف هو **المترجم**. يتلقى سؤالاً غامضاً وسياقاً محدداً، وينتج كوداً دقيقاً.

### استراتيجيات الذكاء (AI Strategies) المطبقة:
1.  **Implicit Caching:** لاحظنا أننا نرسل نفس التعليمات (System Prompt) في كل مرة. جعلناها ثابتة في أول الرسالة ليتمكن Gemini من تخزينها (Cache Hit).
2.  **Strict Mode:** وضعنا `Temperature = 0.0` ليكون الرد حاسماً ومنطقياً وغير إبداعي (لا نريد تأليفاً في الـ SQL).
3.  **Dictionary Injection:** بدلاً من الاعتماد على فهم الـ AI للكلمات العربية، نعطيه قاموساً صريحاً: *"عندما يقول المستخدم 'سعر'، هو يقصد العمود `UnitPrice` وليس `CostPrice`"*.

---

## 4. مثال حقيقي (Real-World Example)

**المستخدم يسأل:** *"مبيعات حي المعادي"*

1.  **SqlGenerationService:** يبني الـ Prompt.
2.  **Dictionary:** يجد أن كلمة "حي" مربوطة بـ `GovName`.
3.  **Validation:** يجد أن المستخدم لم يحدد جدول، لكن السياق يقول نحن في "صفحة المبيعات".
4.  **Prompt:** *"User wants 'Sales' filtered by 'GovName Like المعادي'. Compulsory: Add ComID filter."*
5.  **Generated SQL:**
    ```sql
    SELECT 
        T1.DocNo, 
        T1.DocDate, 
        T1.Total 
    FROM SalesHeader AS T1
    LEFT JOIN GovView AS T2 ON T1.GovId = T2.GovId
    WHERE 
        T2.GovName LIKE N'%المعادي%' 
        AND T1.ComID = @CurrentCompanyId -- (Added automatically via Rules)
    ```

**الأثر:** تحويل كلمة واحدة ("المعادي") إلى جملة `JOIN` معقدة وفلتر أمان بشكل صحيح 100%.
