# 🎯 GenAI Interview Q&A - من مشروع DynamicAi
## (Practical Answers from a Real Production Project)

> **ملاحظة:** هذا الملف يحتوي على إجابات من مشروع **DynamicAi** الفعلي، مرتبة حسب المستويات الأربعة: Beginner, Intermediate, Advanced, Expert.

---

## 📑 فهرس المحتويات

- [🟢 Beginner (Q1-50)](#-beginner-q1-50)
- [🟡 Intermediate (Q51-100)](#-intermediate-q51-100)
- [🔴 Advanced (Q101-150)](#-advanced-q101-150)
- [⚫ Expert (Q151-200)](#-expert-q151-200)

---

# 🟢 Beginner (Q1-50)
*(Fundamentals, Tokenization, Prompting, Parameters, APIs)*

## 1. GenAI Fundamentals

### Q1. What is Generative AI? (مع مثال من المشروع)

**الإجابة النظرية:**
Generative AI هي أنظمة ذكاء اصطناعي تنشئ محتوى جديداً (نص، صور، كود) بدلاً من مجرد تحليل البيانات.

**التطبيق في DynamicAi:**
```text
المستخدم يسأل: "أرني فواتير الشهر الماضي"
↓
DynamicAi يُولّد (Generates): 
SELECT T1.InvoiceNo, T1.InvoiceDate, T1.[المبلغ الإجمالي]
FROM SalesView T1
WHERE MONTH(T1.InvoiceDate) = MONTH(GETDATE()) - 1
AND T1.ComID = @CurrentCompanyId
```

الـ AI لا "يسترجع" الـ SQL من قاعدة بيانات، بل يُنشئه من الصفر بناءً على فهمه للسؤال والـ Schema.

### Q4. What is a Large Language Model (LLM)?

**التطبيق في DynamicAi:**

نستخدم **Google Gemini 2.5 Pro/Flash** كـ LLM أساسي:

```csharp
// من SqlGenerationService.cs
public class SqlGenerationService : ISqlGenerationService
{
    private readonly IChatCompletionService _chatService; // Gemini
    
    public async Task<string> GenerateSqlAsync(string userQuestion, RetrievalContextBundle contextBundle)
    {
        var chatHistory = new ChatHistory();
        chatHistory.AddUserMessage(fullPrompt);
        
        var executionSettings = new GeminiPromptExecutionSettings
        {
            Temperature = 0.0,  // Zero temperature for deterministic output
            TopP = 0.1,         // Low topP for focused sampling
        };
        
        var response = await _chatService.GetChatMessageContentAsync(chatHistory, executionSettings);
    }
}
```

### Q5. Name the major LLM providers and their flagship models.

**في DynamicAi نستخدم:**

| المكون | الموديل | السبب |
|:---|:---|:---|
| SQL Generation | `gemini-2.5-pro` | دقة عالية في توليد SQL |
| Embeddings | `gemini-embedding-001` | تكامل مع Qdrant |
| Cost Optimization | `gemini-2.5-flash` | أرخص للـ simple queries |

### Q8. What is model size and why does it matter?

**التطبيق العملي:**

في DynamicAi، نستخدم **Model Routing** للتحكم في التكلفة:

```text
┌─────────────────────────────────────────────┐
│ Query Complexity Router (Planned)           │
├─────────────────────────────────────────────┤
│ Simple Query (70%) → Gemini Flash (أرخص)   │
│ Medium Query (25%) → Gemini Pro            │
│ Complex Query (5%) → Gemini Pro + Rerank   │
└─────────────────────────────────────────────┘
```

## 2. Tokenization

### Q11. What is tokenization?

**مثال عملي من المشروع:**

```csharp
// من SqlGenerationService.cs - حساب تقريبي للـ Tokens
int totalChars = fullPrompt.Length;
int estimatedTokens = totalChars / 4; // ~4 chars per token (approximate)
double estimatedInputCost = (estimatedTokens / 1_000_000.0) * 0.075;

_logger.LogInformation("[TokenMetrics] TOTAL PROMPT: {Size} chars (~{Tokens} tokens)", 
    totalChars, estimatedTokens);
```

### Q15. Why does tokenization matter for Arabic and non-English languages?

**هذا تحدٍ حقيقي في DynamicAi:**

```text
نفس المعنى:
🇺🇸 "Show invoices" → ~2 tokens
🇸🇦 "أرني الفواتير" → ~6-8 tokens (3-4x أكثر!)

التأثير على المشروع:
- تكلفة أعلى للعملاء العرب
- استهلاك أكبر للـ Context Window
```

**الحل في DynamicAi:**
```csharp
// نستخدم Arabic Aliases في الـ Prompt لتقليل الـ Tokens
sb.AppendLine("COLUMN RESOLUTION: ALIAS with [arColumnLabel] (AS [العربية])");
```

### Q17. How do you count tokens before making an API call?

**التطبيق في DynamicAi:**

```csharp
// من SqlGenerationService.cs - Token Metrics Logging
_logger.LogInformation("[TokenMetrics] ═══════════════ PROMPT BREAKDOWN ═══════════════");
_logger.LogInformation("[TokenMetrics]   Static Rules (System):      {Size,8:N0} chars (~{Tokens,6:N0} tokens)", 
    staticRulesSize, staticRulesSize / 4);
_logger.LogInformation("[TokenMetrics]   Unified Context (Schema):   {Size,8:N0} chars (~{Tokens,6:N0} tokens)", 
    unifiedContextSize, unifiedContextSize / 4);
_logger.LogInformation("[TokenMetrics]   Dictionary (Compact):       {Size,8:N0} chars (~{Tokens,6:N0} tokens)", 
    dictionarySize, dictionarySize / 4);
_logger.LogInformation("[TokenMetrics]   TOTAL PROMPT:               {Size,8:N0} chars (~{Tokens,6:N0} tokens)", 
    totalChars, estimatedTokens);
_logger.LogInformation("[TokenMetrics]   Est. Input Cost:            ${Cost:F6}", estimatedInputCost);
```

### Q18. What is the relationship between tokens and pricing?

**التتبع الفعلي في DynamicAi:**

```csharp
// من GeminiCostTracker.cs
public class GeminiCostTracker : IGeminiCostTracker
{
    // Gemini 2.5 Flash Pricing (USD per 1M tokens) - Dec 2025
    private const double InputCostPerMillion = 0.15;
    private const double OutputCostPerMillion = 0.60;
    private const double CachedCostPerMillion = 0.0375; // ~75% discount on input
    
    public (double TotalCost, double Savings) CalculateCost(int inputTokens, int outputTokens, int cachedTokens)
    {
        int uncachedTokens = Math.Max(0, inputTokens - cachedTokens);
        
        double inputCost = (uncachedTokens / 1_000_000.0) * InputCostPerMillion;
        double cachedCost = (cachedTokens / 1_000_000.0) * CachedCostPerMillion;
        double outputCost = (outputTokens / 1_000_000.0) * OutputCostPerMillion;
        
        return (inputCost + cachedCost + outputCost, savings);
    }
}
```

## 3. Prompting

### Q19. What is a prompt?

**مثال كامل من DynamicAi:**

```text
[STATIC RULES - Cached]
ROLE: Expert ERP Database Architect. 
GOAL: Convert user requests into SAFE T-SQL SELECT queries.
SECURITY(ABSOLUTE): SELECT ONLY. FORBIDDEN: INSERT,UPDATE,DELETE...
...

[DYNAMIC CONTEXT - Per Request]  
--- HIERARCHY & SCHEMA CONTEXT ---
{UnifiedPageContext JSON}

--- DICTIONARY CONTEXT ---
[1] SalesView | InvoiceNo | InvoiceNo | text | رقم الفاتورة | Invoice No
[2] SalesView | CustomerName | CustomerID | dropdown | اسم العميل | Customer Name

--- TASK ---
Q: أرني فواتير الشهر الماضي
A: 
```

### Q24. What is chain-of-thought prompting?

**التطبيق في DynamicAi:**

نطلب من الموديل إضافة تعليقات توضح منطقه:

```csharp
// من SqlGenerationService.cs - BuildStaticSystemRules
sb.AppendLine("COMMENTS(MANDATORY): Every SELECT column and JOIN must have comment: " +
    "--SRC:Table.Col|LIST:ListName|DRO:DroModel/NA|RULE:Step|IDX:i|MSG:Info");
```

**الناتج:**
```sql
SELECT 
    T1.InvoiceNo, --SRC:SalesView.InvoiceNo|LIST:SalesPage|DRO:NA|RULE:8C|IDX:1|MSG:Direct column
    T2.CustomerName --SRC:Customers.CustomerName|LIST:SalesPage|DRO:Customers|RULE:8D|IDX:2|MSG:Dropdown join
FROM SalesView T1
JOIN Customers T2 ON T1.CustomerID = T2.CustomerID --JOIN:CustomerID→CustomerID
WHERE T1.ComID = @CurrentCompanyId
```

### Q26. What makes a bad prompt?

**مثال من تطوير DynamicAi:**

```text
❌ BEFORE (Bad):
"Generate SQL for the user question"

✅ AFTER (Good - من الكود الفعلي):
"ROLE: Expert ERP Database Architect.
GOAL: Convert user requests into SAFE T-SQL SELECT queries with MANDATORY ARABIC ALIASES.
OUTPUT: T-SQL (markdown) OR CLARIFICATION_NEEDED. Never both.
SECURITY(ABSOLUTE): SELECT ONLY. FORBIDDEN: INSERT,UPDATE,DELETE,MERGE,DROP..."
```

### Q28. What is prompt injection?

**الحماية في DynamicAi:**

```csharp
// من SqlGenerationService.cs
sb.AppendLine("SECURITY(ABSOLUTE):1)SELECT ONLY. FORBIDDEN: INSERT,UPDATE,DELETE,MERGE,DROP,ALTER,CREATE,TRUNCATE,EXEC,sp_,xp_,GRANT,REVOKE.");
sb.AppendLine("If requested→Output ONLY: REFUSED: عذراً، هذا النظام مخصص للاستعلامات فقط (SELECT). يرجى التواصل مع مسؤول النظام.");
```

**طبقة إضافية - SQL Validation:**
```csharp
// من SqlValidatorService.cs
public class SqlValidatorService : ISqlValidatorService
{
    // يفحص الـ SQL المولد ويرفض أي عملية غير SELECT
    // مثبت بـ 62 Unit Test ✅
}
```

## 4. Model Parameters

### Q29. What is temperature in LLMs?

**الإعدادات الفعلية في DynamicAi:**

```csharp
// من SqlGenerationService.cs
var executionSettings = new GeminiPromptExecutionSettings
{
    Temperature = 0.0,  // Zero temperature for deterministic output
    TopP = 0.1,         // Low topP for more focused sampling
    CachedContent = cachedContentId
};
```

**لماذا Temperature = 0?**
```text
SQL Generation requires DETERMINISTIC output:
- نفس السؤال → نفس الـ SQL كل مرة
- لا مجال للإبداع في بناء الجملة
- الأخطاء في SQL كارثية (بيانات خاطئة أو أمنية)
```

### Q36. What is the context window?

**كيف نتعامل معه في DynamicAi:**

```text
┌─────────────────────────────────────────────────────────────┐
│              Context Window Management                       │
├─────────────────────────────────────────────────────────────┤
│ Gemini 2.5 Pro Context: 1M+ tokens                          │
│                                                              │
│ Our Prompt Structure:                                        │
│ ├── Static Rules (Cached): ~1,500 tokens (3-4K chars)       │
│ ├── Schema Context: ~2,000 tokens                           │
│ ├── Dictionary: ~500-1,500 tokens                           │
│ ├── Business Rules: ~200 tokens                             │
│ └── User Question: ~50 tokens                               │
│                                                              │
│ TOTAL: ~4,000-6,000 tokens per request                      │
│                                                              │
│ Optimization: "Prompt Diet" reduced from ~10K to ~4K tokens │
└─────────────────────────────────────────────────────────────┘
```

**الكود:**
```csharp
// من SqlGenerationService.cs - Prompt Diet
sb.AppendLine("ROLE: Expert ERP Database Architect...");  // Compressed rules
// بدلاً من شرح مفصل، نستخدم اختصارات ذكية
```

## 5. API & Integration

### Q39. What are the message roles in chat completions API?

**التطبيق في DynamicAi:**

```csharp
// من SqlGenerationService.cs
var chatHistory = new ChatHistory();

// User Message (includes both system rules & user question)
chatHistory.AddUserMessage(fullPrompt);

// في حالة Conversation History (QueryOrchestrator):
// chatHistory.AddAssistantMessage(previousResponse);
// chatHistory.AddUserMessage(followUpQuestion);
```

### Q40. Why is the API stateless and what does that mean?

**التحدي في DynamicAi:**

```text
المستخدم: "أرني فواتير الشهر الماضي"
AI: [SQL للفواتير]

المستخدم: "الآن احذف منها أقل من 1000"
↓
المشكلة: الـ API لا يعرف ماذا يعني "منها" بدون سياق!
```

**الحل - Semantic Caching:**
```csharp
// من QueryCacheService.cs
public async Task<CachedQueryResult?> TryGetCachedSqlAsync(
    float[] questionEmbedding,
    string tenantId,
    int companyId)
{
    // نخزن السياق السابق في Qdrant
    // عند السؤال التالي، نسترجع السياق المشابه
}
```

### Q43. What is streaming in LLM APIs?

**قرار تصميمي في DynamicAi:**

```text
لماذا لم نستخدم Streaming؟

في SQL Generation:
- الـ SQL يجب أن يكون كاملاً قبل التنفيذ
- لا فائدة من "نصف SQL"
- Validation يحتاج الـ SQL الكامل

لكن في UI:
- نستخدم Loading States بدلاً من Streaming
- نعرض "جاري تحليل السؤال..." ثم "جاري توليد SQL..."
```

---

# 🟡 Intermediate (Q51-100)
*(Transformers, Embeddings, Vector DB, RAG, Fine-tuning)*

## 6. Transformer Architecture

### Q51. What is the Transformer architecture?

**كيف نستفيد منه في DynamicAi:**

```text
Gemini (Decoder-only Transformer)
       ↓
Self-Attention يفهم العلاقات:
- "فواتير الشهر الماضي" → يربط "الشهر" بـ MONTH()
- "العميل أحمد" → يربط "أحمد" بـ CustomerName
       ↓
يولد SQL token by token
```

## 7. Embeddings

### Q66. What are embeddings?

**التطبيق المزدوج في DynamicAi:**

```text
┌─────────────────────────────────────────────────────────────┐
│           Embedding Usage in DynamicAi                       │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│ 1. Schema Retrieval (QdrantRetrievalService):               │
│    User Question → Embedding → Search Qdrant → Get Schema   │
│                                                              │
│ 2. Query Caching (QueryCacheService):                       │
│    User Question → Embedding → Search Cache → Return SQL    │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

### Q68. Name popular embedding models.

**ما نستخدمه:**

```csharp
// Gemini Embedding (3072 dimensions)
// من QueryCacheService.cs
await _qdrantClient.CreateCollectionAsync(
    _collectionName,
    new VectorParams
    {
        Size = 3072, // Gemini embedding size
        Distance = Distance.Cosine
    });
```

### Q71. What is cosine similarity?

**الاستخدام الفعلي:**

```csharp
// من QueryCacheService.cs
private readonly float _similarityThreshold; // default 0.92

var searchResult = await _qdrantClient.SearchAsync(
    _collectionName,
    questionEmbedding,
    filter: filter,
    limit: 1,
    scoreThreshold: _similarityThreshold  // Cosine Similarity
);

if (searchResult.Any())
{
    _logger.LogInformation("[QueryCache] ✓ Cache HIT! Score: {Score:F4}", 
        searchResult.First().Score);
}
```

## 8. Vector Databases

### Q76. What is a vector database?

**Qdrant في DynamicAi:**

```text
┌─────────────────────────────────────────────────────────────┐
│                 Qdrant Collections                           │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│ 1. erp_schema_collection:                                   │
│    - Stores: Schema metadata, table definitions             │
│    - Used by: QdrantRetrievalService                        │
│    - Size: ~5,000+ documents                                │
│                                                              │
│ 2. query_cache:                                             │
│    - Stores: Question embeddings + Generated SQL            │
│    - Used by: QueryCacheService                             │
│    - TTL: 7 days                                            │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

### Q80. What is HNSW and why is it popular?

**الإعداد في DynamicAi:**

```csharp
// من QdrantVectorDbService.cs - Collection Creation
await _qdrantClient.CreateCollectionAsync(
    collectionName,
    new VectorParams
    {
        Size = 3072,
        Distance = Distance.Cosine
        // Qdrant uses HNSW by default
    });
```

### Q84. What is metadata filtering in vector search?

**التطبيق الفعلي للـ Multi-tenancy:**

```csharp
// من QueryCacheService.cs - Tenant Isolation
var filter = new Filter();
filter.Must.Add(new Condition
{
    Field = new FieldCondition
    {
        Key = "tenant_id",
        Match = new Match { Keyword = tenantId }
    }
});
filter.Must.Add(new Condition
{
    Field = new FieldCondition
    {
        Key = "company_id",
        Match = new Match { Integer = companyId }
    }
});

var searchResult = await _qdrantClient.SearchAsync(
    _collectionName,
    questionEmbedding,
    filter: filter,  // ← Pre-filtering by tenant
    limit: 1,
    scoreThreshold: _similarityThreshold
);
```

## 9. RAG (Retrieval-Augmented Generation)

### Q86. What is RAG?

**الـ Pipeline الكامل في DynamicAi:**

```text
┌─────────────────────────────────────────────────────────────┐
│              DynamicAi RAG Pipeline                          │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│ 1. User Question: "أرني فواتير العميل أحمد"                 │
│                    ↓                                         │
│ 2. QueryOrchestrator:                                        │
│    ├── Check Cache (QueryCacheService)                      │
│    │   └── If HIT → Return cached SQL                       │
│    │                                                         │
│ 3. If MISS → Retrieval (QdrantRetrievalService):            │
│    ├── Embed user question                                  │
│    ├── Search Qdrant for relevant schema                    │
│    └── Return: UnifiedPageContext, DictionaryEntries        │
│                    ↓                                         │
│ 4. Generation (SqlGenerationService):                        │
│    ├── Build prompt with retrieved context                  │
│    ├── Send to Gemini                                       │
│    └── Return: Generated SQL                                │
│                    ↓                                         │
│ 5. Validation (SqlValidatorService):                         │
│    └── Security checks + Syntax validation                  │
│                    ↓                                         │
│ 6. Execution + Cache Storage                                 │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

### Q87. Why use RAG instead of fine-tuning?

**قرارنا في DynamicAi:**

```text
لماذا RAG وليس Fine-tuning؟

✅ RAG (ما نستخدمه):
- الـ Schema يتغير باستمرار (CDC)
- كل عميل له Schema مختلف (Multi-tenant)
- لا نحتاج retraining عند إضافة جداول

❌ Fine-tuning (لاحقاً):
- نجمع Golden Dataset من Feedback
- Fine-tuning على أنماط SQL المتكررة
- لكن RAG يبقى للـ Schema الديناميكي
```

### Q88. What is the basic RAG pipeline?

**الكود الفعلي من QueryOrchestrator:**

```csharp
// من QueryOrchestrator.cs - HandleQueryAsync (مبسط)

// Layer 0: Cache Check
var cachedResult = await _cacheService.TryGetCachedSqlAsync(embedding, tenantId, companyId);
if (cachedResult != null)
{
    return cachedResult.Sql; // Cache HIT - Skip LLM!
}

// Layer 1: Retrieval
var contextBundle = await _retrievalService.RetrieveContextAsync(userQuestion, ...);

// Layer 2: Generation
var generatedSql = await _sqlGenService.GenerateSqlAsync(userQuestion, contextBundle);

// Layer 3: Validation
var validatedSql = await _validatorService.ValidateAndRewriteAsync(generatedSql);

// Layer 4: Execution
var results = await _executor.ExecuteAsync(validatedSql);

// Layer 5: Cache Storage
await _cacheService.StoreCachedSqlAsync(id, userQuestion, embedding, validatedSql, ...);
```

### Q89. What is chunking and why is it necessary?

**في DynamicAi نستخدم Semantic Chunking:**

```text
بدلاً من تقسيم عشوائي، نقسم حسب الـ ERP Structure:

┌─────────────────────────────────────────────┐
│ Page (Screen) = Unit of Work               │
│ ├── PageAttribute (Master Table)           │
│ ├── PageDetail (Detail Tables)             │
│ └── ListSetups (Column Definitions)        │
└─────────────────────────────────────────────┘

كل "Chunk" = Page كاملة مع كل الـ metadata الخاصة بها
هذا أفضل من splitting عشوائي لأن السياق محفوظ!
```

### Q93. What is top-k retrieval?

**الإعدادات في DynamicAi:**

```csharp
// من QdrantRetrievalService.cs
var searchResult = await _qdrantClient.SearchAsync(
    collectionName,
    embedding,
    filter: filter,
    limit: 5,  // Top-K = 5
    scoreThreshold: 0.5f  // Minimum similarity
);
```

## 10. Fine-tuning

### Q96-99. Fine-tuning Basics

**الـ Golden Dataset Pipeline في DynamicAi:**

```csharp
// من FeedbackService.cs
public async Task<bool> ProcessLikeFeedbackAsync(int queryLogId, string? userComment)
{
    // عند Like → حفظ كـ Training Example
    var trainingExample = new TrainingExample
    {
        NaturalQuery = queryLog.NaturalQuery,
        GeneratedSql = queryLog.GeneratedSql,
        SchemaContext = queryLog.SchemaContext,
        IsVerified = true,  // Ready for fine-tuning
        FeedbackType = "Like"
    };
    
    await _trainingRepository.AddAsync(trainingExample);
}
```

**Export Format (JSONL for Fine-tuning):**
```json
{"messages":[
  {"role":"user","content":"أرني فواتير الشهر الماضي مع سياق الـ Schema..."},
  {"role":"assistant","content":"SELECT T1.InvoiceNo, T1.InvoiceDate..."}
]}
```

---

# 🔴 Advanced (Q101-150)
*(Agents, Advanced RAG, Tool Use)*

## 11. Agents & Tool Use

### Q126-128. LLM Agents

**DynamicAi ليس Agent بالمعنى الكامل، لكن يحتوي على عناصر وكيل:**

```text
┌─────────────────────────────────────────────────────────────┐
│            DynamicAi "Agent-like" Behavior                   │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│ Observation: User question + Current page context           │
│      ↓                                                       │
│ Reasoning: Clarification Loop (Q21 in original guide)       │
│   "Is the question clear? Do I have all context?"           │
│      ↓                                                       │
│ Action:                                                      │
│   ├── Generate SQL (if clear)                               │
│   └── Ask clarification (if ambiguous)                      │
│      ↓                                                       │
│ Tool Use: SQL Execution against database                     │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

**من الـ Prompt:**
```csharp
sb.AppendLine("AMBIGUITY HANDLING: If truly ambiguous→ask ONE simple user-friendly question like 'هل تقصد X أو Y؟' in ≤15 words.");
```

---

# ⚫ Expert (Q151-200)
*(Production, Security, Cost, Observability)*

## 12. Production Deployment

### Q151. Key considerations for deploying LLMs to production

**كيف نطبقها في DynamicAi:**

| Consideration | التطبيق في DynamicAi |
|:---|:---|
| **Latency** | Semantic Caching (Cache HIT = ~100ms vs LLM = 20s+) |
| **Cost** | GeminiCostTracker + Prompt Diet (60% reduction) |
| **Reliability** | Polly Retry Policies |
| **Security** | SqlValidatorService + SecurityVisitor |
| **Monitoring** | Structured Logging (Serilog) |

### Q154. What is quantization for inference?

**لا نحتاجها حالياً:**

```text
لماذا؟
- نستخدم Gemini API (managed)
- لا نشغل الموديل محلياً
- Google تتولى الـ optimization

لكن إذا استخدمنا Self-hosted model مستقبلاً:
- سنستخدم QLoRA/GPTQ للـ fine-tuning
- INT8 quantization للـ inference
```

## 13. Cost Optimization

### Q161. How do you optimize LLM costs at scale?

**الاستراتيجيات المطبقة في DynamicAi:**

```text
┌─────────────────────────────────────────────────────────────┐
│              Cost Optimization Strategies                    │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│ 1. Semantic Caching (QueryCacheService):                    │
│    - Similar questions → Same SQL from cache                │
│    - Saves 100% of LLM cost for cache hits                  │
│    - Threshold: 0.92 cosine similarity                      │
│                                                              │
│ 2. Prompt Diet:                                             │
│    - Reduced from ~10K to ~4K tokens (60% reduction)        │
│    - Compact Dictionary Format                              │
│    - Compressed rules syntax                                │
│                                                              │
│ 3. Gemini Implicit Caching:                                 │
│    - Static rules cached on Google servers                  │
│    - 75% discount on cached tokens                          │
│                                                              │
│ 4. Smart Context Selection:                                 │
│    - Only retrieve relevant schema (not all tables)         │
│    - Reduces context size significantly                     │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

**الكود:**
```csharp
// من GeminiCostTracker.cs
private const double CachedCostPerMillion = 0.0375; // ~75% discount on input
```

### Q162. What is semantic caching?

**التنفيذ الكامل:**

```csharp
// من QueryCacheService.cs

/// <summary>
/// Searches for cached SQL based on semantic similarity.
/// If found above threshold, returns cached result instead of calling LLM.
/// </summary>
public async Task<CachedQueryResult?> TryGetCachedSqlAsync(
    float[] questionEmbedding,
    string tenantId,
    int companyId)
{
    var searchResult = await _qdrantClient.SearchAsync(
        _collectionName,
        questionEmbedding,
        filter: filter,  // Tenant isolation
        limit: 1,
        scoreThreshold: _similarityThreshold  // 0.92
    );

    if (searchResult.Any())
    {
        var topResult = searchResult.First();
        _logger.LogInformation("[QueryCache] ✓ Cache HIT! Score: {Score:F4}", topResult.Score);
        
        return new CachedQueryResult
        {
            Sql = topResult.Payload["generated_sql"].StringValue,
            Score = topResult.Score,
            OriginalQuestion = topResult.Payload["original_question"].StringValue
        };
    }

    return null; // Cache MISS → proceed to LLM
}
```

## 14. Security & Guardrails

### Q166. What are the main security risks with LLM applications?

**كيف نتعامل معها في DynamicAi:**

| Risk | الحماية في DynamicAi |
|:---|:---|
| **Prompt Injection** | Static rules + Validation layer |
| **SQL Injection** | SqlValidatorService + SecurityVisitor |
| **Data Leakage** | Multi-tenant filtering (ComID) |
| **Unauthorized Actions** | SELECT ONLY enforcement |

### Q167. What is prompt injection and how do you prevent it?

**الحماية متعددة الطبقات:**

```text
┌─────────────────────────────────────────────────────────────┐
│              Prompt Injection Defense                        │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│ Layer 1: Prompt Rules (SqlGenerationService)                │
│   "If requested→Output ONLY: REFUSED: عذراً..."            │
│                                                              │
│ Layer 2: SQL Validation (SqlValidatorService)               │
│   - Parses SQL using SMO                                    │
│   - Rejects non-SELECT statements                           │
│   - 62 Unit Tests ✅                                         │
│                                                              │
│ Layer 3: SQL Hardening (SqlHardeningVisitor)                │
│   - Adds ComID filter automatically                         │
│   - Adds TOP 100 if needed
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

**الكود:**
```csharp
// من SqlGenerationService.cs
sb.AppendLine("SECURITY(ABSOLUTE):1)SELECT ONLY. FORBIDDEN: INSERT,UPDATE,DELETE,MERGE,DROP,ALTER,CREATE,TRUNCATE,EXEC,sp_,xp_,GRANT,REVOKE.");
```

### Q170. What are guardrail frameworks?

**الـ Custom Guardrails في DynamicAi:**

```csharp
// من SqlValidatorService.cs (Custom Implementation)
public class SqlValidatorService : ISqlValidatorService
{
    public (bool IsValid, string Message) Validate(string sql)
    {
        // 1. Check for forbidden keywords
        // 2. Parse with SMO
        // 3. Validate statement type
        // 4. Check allowed tables
        // 5. Verify ComID filter
    }
}

// من SqlHardeningVisitor.cs
public class SqlHardeningVisitor : TSqlFragmentVisitor
{
    // Automatically modifies SQL for security:
    // - Adds WHERE ComID = @CurrentCompanyId
    // - Adds TOP 100 if needed
}
```

## 15. Monitoring & Observability

### Q176. What should you monitor in LLM applications?

**الـ Metrics في DynamicAi:**

```csharp
// من GeminiCostTracker.cs - Session Tracking
public (long TotalInputTokens, long TotalOutputTokens, double TotalCostUsd, int CallCount) GetSessionSummary()
{
    return (_sessionInputTokens, _sessionOutputTokens, _sessionTotalCost, _sessionCallCount);
}

// من QueryOrchestrator.cs - Request Metrics
_logger.LogInformation("QueryOrchestrator TotalDuration={Duration}ms Status={Status}", 
    totalDuration, statusCode);
```

**الـ Logs المنظمة:**
```csharp
// Serilog Structured Logging
_logger.LogInformation(
    "GeminiUsage RequestId={RequestId} Input={InputTokens} Output={OutputTokens} Cached={CachedTokens} CostUSD={Cost:F6}",
    requestId, inputTokens, outputTokens, cachedTokens, totalCost);
```

### Q177. What is LLM tracing?

**الـ Layered Logging في DynamicAi:**

```text
╔════════════════════════════════════════════════════════════╗
║              [Layer 1: RETRIEVAL] - STARTING               ║
╚════════════════════════════════════════════════════════════╝
[Retrieval] User Question: "أرني فواتير الشهر الماضي"
[Retrieval] ✓ Found 3 relevant schemas
[Retrieval] Duration: 150ms
╔════════════════════════════════════════════════════════════╗
║            [Layer 2: SQL GENERATION] - STARTING            ║
╚════════════════════════════════════════════════════════════╝
[SqlGeneration] STEP 1: Analyzing Context Bundle...
[SqlGeneration] STEP 2: Building AI prompt...
[TokenMetrics] TOTAL PROMPT: 5,234 chars (~1,308 tokens)
[SqlGeneration] STEP 3: Sending to Gemini...
[GeminiCache] ✓ CACHE HIT! 1,200 tokens served from cache
[SqlGeneration] ✓ AI response in 2,340ms
╔════════════════════════════════════════════════════════════╗
║            [Layer 2: SQL GENERATION] - COMPLETED ✓         ║
╚════════════════════════════════════════════════════════════╝
```

---

## 📊 ملخص الميزات المطبقة

```text
┌─────────────────────────────────────────────────────────────┐
│         DynamicAi - GenAI Features Summary                   │
├─────────────────────────────────────────────────────────────┤
│ ✅ RAG (Retrieval-Augmented Generation)                     │
│ ✅ Vector Database (Qdrant)                                 │
│ ✅ Semantic Caching                                         │
│ ✅ Prompt Engineering (Zero-Temperature, Chain-of-Thought)  │
│ ✅ Token/Cost Tracking                                      │
│ ✅ Multi-tenant Support                                     │
│ ✅ SQL Validation & Security                                │
│ ✅ Feedback System (Golden Dataset Pipeline)                │
│ ✅ Structured Logging/Observability                         │
│ ✅ Clean Architecture                                       │
├─────────────────────────────────────────────────────────────┤
│ 🚧 Planned: Reranking, Hybrid Search, Fine-tuning           │
│ 🚧 Planned: OpenTelemetry, Model Routing                    │
└─────────────────────────────────────────────────────────────┘
```

---

## 🔗 المراجع

- **Original Guide:** Hesham Haroon - GenAI Interview Guide (200 Questions)
- **Project:** DynamicAi - Natural Language to SQL with RAG
- **Stack:** C# .NET 8, Angular 17+, Qdrant, Google Gemini

---

*Created: 2026-01-01*
*Project: DynamicAi*
