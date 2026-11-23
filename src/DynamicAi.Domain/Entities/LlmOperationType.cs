namespace DynamicAi.Domain.Entities
{
    /// <summary>
    /// أنواع العمليات التي تستدعي LLM أو Embedding API.
    /// Provider-Agnostic — يعمل مع أي مزود (Gemini, OpenAI, Anthropic).
    /// </summary>
    public enum LlmOperationType
    {
        /// <summary>توليد SQL من اللغة الطبيعية</summary>
        SqlGeneration = 0,

        /// <summary>محادثة عامة (Chat Handler)</summary>
        Chat = 1,

        /// <summary>تحليل تنبؤي (Predict Handler)</summary>
        Prediction = 2,

        /// <summary>تكثيف السؤال (Query Condenser - Layer 0.5)</summary>
        QueryCondenser = 3,

        /// <summary>تصنيف النية (Intent Classification via LLM)</summary>
        IntentClassification = 4,

        /// <summary>توليد تضمينات (Embedding Generation)</summary>
        Embedding = 5,

        /// <summary>بذر البيانات التدريبية الأولية (Data Seeding)</summary>
        DataSeeding = 6
    }
}
