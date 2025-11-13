namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// مسؤول عن "استرجاع" السياق (الحقائق)
    /// ذات الصلة بسؤال المستخدم من قاعدة بيانات المتجهات (Qdrant).
    /// </summary>
    public interface IRagRetrievalService
    {
        /// <summary>
        /// يبحث في Qdrant عن المستندات (Schema/Data) ذات الصلة بالسؤال.
        /// </summary>
        /// <param name="userQuestion">السؤال الأصلي للمستخدم (e.g., "ما هي مبيعاتي؟")</param>
        /// <param name="tenantId">معرف "الشركة" (للفلترة وعزل البيانات)</param>
        /// <param name="limit">الحد الأقصى لعدد "الحقائق" المراد استرجاعها</param>
        /// <returns>قائمة من "الحقائق" النصية (JSON rows أو CREATE scripts)</returns>
        Task<List<string>> RetrieveContextAsync(
            string userQuestion,
            string tenantId,
            int limit = 5
        );
    }
}
