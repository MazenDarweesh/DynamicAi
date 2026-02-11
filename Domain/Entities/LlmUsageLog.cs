namespace DynamicAi.Domain.Entities
{
    /// <summary>
    /// سجل استخدام LLM/Embedding API — Provider-Agnostic.
    /// يخزن تفاصيل كل استدعاء: tokens، تكلفة، مزود، موديل.
    /// يدعم أي مزود (Gemini, OpenAI, Anthropic) عبر حقلي ProviderName و ModelName.
    /// </summary>
    public class LlmUsageLog
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>معرف الطلب الأصلي (QueryLog.Id أو GUID مؤقت)</summary>
        public string RequestId { get; set; } = string.Empty;

        /// <summary>نوع العملية (SqlGeneration, Chat, Embedding, etc.)</summary>
        public LlmOperationType OperationType { get; set; }

        /// <summary>اسم المزود (e.g. "Gemini", "OpenAI", "Anthropic")</summary>
        public string ProviderName { get; set; } = string.Empty;

        /// <summary>اسم الموديل المستخدم (e.g. "gemini-2.5-flash-lite", "gpt-4o-mini")</summary>
        public string ModelName { get; set; } = string.Empty;

        /// <summary>عدد tokens المدخلة</summary>
        public int InputTokens { get; set; }

        /// <summary>عدد tokens المخرجة (0 للـ Embedding)</summary>
        public int OutputTokens { get; set; }

        /// <summary>عدد tokens المخزنة مؤقتاً (Context Caching)</summary>
        public int CachedTokens { get; set; }

        /// <summary>التكلفة المحسوبة بالدولار الأمريكي</summary>
        public decimal CostUsd { get; set; }

        /// <summary>معرف الـ Tenant (اختياري)</summary>
        public string? TenantId { get; set; }

        /// <summary>معرف الشركة (اختياري)</summary>
        public int? CompanyId { get; set; }

        /// <summary>وقت الإنشاء (UTC)</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
