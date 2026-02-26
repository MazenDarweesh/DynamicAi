using System.Collections.Generic;
using System.Threading.Tasks;
using DynamicAi.Application.Models;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// "العقل المدبر" (The Orchestrator) لخط أنابيب الاستعلام (RAG Pipeline).
    /// مسؤول عن تنسيق جميع الطبقات (Layers 0.5-5)
    /// لتحويل سؤال المستخدم إلى بيانات آمنة.
    /// </summary>
    public interface IQueryOrchestrator
    {
        /// <summary>
        /// يعالج سؤال المستخدم بالكامل، مروراً بجميع طبقات الأمان والتوليد.
        /// </summary>
        /// <param name="userQuestion">السؤال الأصلي للمستخدم (e.g., "ما هي مبيعاتي؟")</param>
        /// <param name="tenantId">معرف "الشركة" (لعزل البيانات)</param>
        /// <param name="comID">معرف إضافي اختياري (اختياري)</param>
        /// <param name="previousQuestion">السؤال السابق (Optional, for Query Condensing)</param>
        /// <param name="previousSql">كود SQL السابق (Optional, for Subquery Wrapping)</param>
        /// <returns>نتيجة الاستعلام (البيانات + Metadata).</returns>
        Task<QueryExecutionResult> HandleQueryAsync(
            string userQuestion,
            string sessionId,
            string tenantId,
            int? comID = null,
            string? previousQuestion = null,
            string? previousSql = null,
            CancellationToken ct = default);
    }
}
