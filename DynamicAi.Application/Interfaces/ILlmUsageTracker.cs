using DynamicAi.Domain.Entities;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// واجهة تتبع استخدام LLM — Provider-Agnostic.
    /// تتتبع كل استدعاء LLM أو Embedding مع حفظ في DB.
    /// تعمل مع أي مزود (Gemini, OpenAI, Anthropic) بدون تغيير كود.
    /// </summary>
    public interface ILlmUsageTracker
    {
        /// <summary>
        /// يسجل استدعاء LLM (Chat, SQL Generation, Prediction, etc.).
        /// يحسب التكلفة تلقائياً من الأسعار المكونة في appsettings.json.
        /// </summary>
        /// <param name="operationType">نوع العملية</param>
        /// <param name="requestId">معرف الطلب الأصلي</param>
        /// <param name="inputTokens">عدد tokens المدخلة</param>
        /// <param name="outputTokens">عدد tokens المخرجة</param>
        /// <param name="cachedTokens">عدد tokens المخزنة مؤقتاً (Context Caching)</param>
        /// <param name="tenantId">معرف الـ Tenant (اختياري)</param>
        /// <param name="companyId">معرف الشركة (اختياري)</param>
        Task TrackLlmCallAsync(
            LlmOperationType operationType,
            string requestId,
            int inputTokens,
            int outputTokens,
            int cachedTokens = 0,
            string? tenantId = null,
            int? companyId = null);

        /// <summary>
        /// يسجل استدعاء Embedding API.
        /// </summary>
        /// <param name="operationType">نوع العملية (عادة Embedding)</param>
        /// <param name="requestId">معرف الطلب الأصلي</param>
        /// <param name="tokenCount">عدد tokens المدخلة للتضمين</param>
        /// <param name="tenantId">معرف الـ Tenant (اختياري)</param>
        /// <param name="companyId">معرف الشركة (اختياري)</param>
        Task TrackEmbeddingCallAsync(
            LlmOperationType operationType,
            string requestId,
            int tokenCount,
            string? tenantId = null,
            int? companyId = null);

        /// <summary>
        /// يحصل على إجمالي تكلفة اليوم بالدولار.
        /// يُستخدم من قبل Middleware لفحص الميزانية.
        /// </summary>
        Task<decimal> GetDailyTotalCostAsync(DateTime date);

        /// <summary>
        /// يحصل على ملخص الجلسة الحالية (In-Memory).
        /// متوافق مع QueryOrchestrator للـ backward compatibility.
        /// </summary>
        LlmSessionSummary GetSessionSummary();

        /// <summary>
        /// يعيد تعيين عدادات الجلسة الحالية.
        /// </summary>
        void ResetSession();
    }

    /// <summary>
    /// ملخص استخدام الجلسة الحالية (In-Memory) — Provider-Agnostic.
    /// </summary>
    public class LlmSessionSummary
    {
        public long TotalInputTokens { get; set; }
        public long TotalOutputTokens { get; set; }
        public long TotalCachedTokens { get; set; }
        public double TotalCostUsd { get; set; }
        public int TotalCallCount { get; set; }
    }
}
