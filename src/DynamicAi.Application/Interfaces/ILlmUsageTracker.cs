using DynamicAi.Application.Models;
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
        /// يحسب التكلفة تلقائياً من الأسعار المكونة في appsettings.json ويعيد التكلفة بالدولار.
        /// </summary>
        Task<decimal> TrackLlmCallAsync(
            LlmOperationType operationType,
            string requestId,
            int inputTokens,
            int outputTokens,
            int cachedTokens = 0,
            string? tenantId = null,
            int? companyId = null,
            string? modelName = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// يسجل استدعاء Embedding API ويعيد التكلفة بالدولار.
        /// </summary>
        Task<decimal> TrackEmbeddingCallAsync(
            LlmOperationType operationType,
            string requestId,
            int tokenCount,
            string? tenantId = null,
            int? companyId = null,
            string? modelName = null,
            CancellationToken cancellationToken = default);

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
}
