using DynamicAi.Application.Models; // (جديد)
using System.Threading.Tasks;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// (مُعدل) مسؤول عن تنفيذ "البحث الذكي متعدد الخطوات"
    /// لجلب السياق الكامل (Schema, Data, Rules).
    /// </summary>
    public interface IRagRetrievalService
    {
        /// <summary>
        /// (مُعدل) يبحث في Qdrant باستخدام خوارزمية الفهم العميق.
        /// </summary>
        /// <param name="userQuestion">السؤال الأصلي للمستخدم (e.g., "ما هي مبيعاتي؟")</param>
        /// <param name="tenantId">معرف "الشركة" (للفلترة وعزل البيانات)</param>
        /// <returns>"سلة سياق" (Context Bundle) غنية جاهزة للـ AI.</returns>
        Task<RetrievalContextBundle> RetrieveContextAsync(
            string userQuestion,
            string tenantId
        );

        /// <summary>
        /// Retrieves FULL page context for LLM caching (used by Gemini Page Cache Service).
        /// Returns ALL dictionary entries grouped by ListName + minimal schema context.
        /// </summary>
        /// <param name="pageId">The page ID to retrieve full context for</param>
        /// <param name="tenantId">Tenant ID for data isolation</param>
        /// <returns>Complete page context for Gemini caching</returns>
        Task<FullPageContextForLlmCache> GetFullPageContextForLlmCachingAsync(
            int pageId,
            string tenantId
        );
    }
}