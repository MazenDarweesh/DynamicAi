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
    }
}