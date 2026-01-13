namespace DynamicAi.Domain.Entities
{
    public class QueryLog
    {
        public Guid Id { get; set; }
        public string RawQuestion { get; set; } = string.Empty;
        public string? GeneratedSql { get; set; }
        public bool WasSuccessful { get; set; }
        public string? ErrorMessage { get; set; }

        /// <summary>سياق الـ Schema المستخدم وقت توليد الـ SQL (JSON)</summary>
        public string? SchemaContext { get; set; }

        /// <summary>معرف الـ Tenant</summary>
        public string? TenantId { get; set; }

        /// <summary>معرف الشركة</summary>
        public int? CompanyId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        #region Observability Fields (Phase A)

        /// <summary>وقت تنفيذ الطلب بالمللي ثانية</summary>
        public int LatencyMs { get; set; }

        /// <summary>نوع المعالج: "SQL" | "Predict" | "Chat"</summary>
        public string? HandlerType { get; set; }

        /// <summary>تكلفة استدعاء Gemini API</summary>
        public decimal? GeminiCost { get; set; }

        /// <summary>هل تم استخدام LLM بدلاً من Qdrant؟</summary>
        public bool WasFallback { get; set; } = false;

        /// <summary>درجة ثقة الـ Router (0.0 - 1.0)</summary>
        public float? ConfidenceScore { get; set; }

        /// <summary>هل السؤال غامض ويحتاج توضيح؟</summary>
        public bool IsAmbiguous { get; set; } = false;

        /// <summary>حالة التقييم: "Like" | "Dislike" | null</summary>
        public string? FeedbackStatus { get; set; }

        /// <summary>عدد tokens المدخلة</summary>
        public int? InputTokens { get; set; }

        /// <summary>عدد tokens المخرجة</summary>
        public int? OutputTokens { get; set; }

        #endregion
    }
}
