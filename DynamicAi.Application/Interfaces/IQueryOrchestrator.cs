using System.Collections.Generic;
using System.Threading.Tasks;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// "العقل المدبر" (The Orchestrator) لخط أنابيب الاستعلام (RAG Pipeline).
    /// مسؤول عن تنسيق جميع الطبقات (Layers 1-5)
    /// لتحويل سؤال المستخدم إلى بيانات آمنة.
    /// </summary>
    public interface IQueryOrchestrator
    {
        /// <summary>
        /// يعالج سؤال المستخدم بالكامل، مروراً بجميع طبقات الأمان والتوليد.
        /// </summary>
        /// <param name="userQuestion">السؤال الأصلي للمستخدم (e.g., "ما هي مبيعاتي؟")</param>
        /// <param name="tenantId">معرف "الشركة" (لعزل البيانات)</param>
        /// <returns>البيانات النهائية (IEnumerable<dynamic>) من قاعدة البيانات.</returns>
        Task<IEnumerable<dynamic>> HandleQueryAsync(string userQuestion, string tenantId);
    }
}