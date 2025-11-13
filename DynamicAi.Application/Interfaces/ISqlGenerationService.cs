namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// (القلب النابض للنظام - Layer 2)
    /// مسؤول عن بناء الـ Prompt وتوليد كود SQL "خام" (Raw)
    /// باستخدام موديل المحادثة (LLM).
    /// </summary>
    public interface ISqlGenerationService
    {
        /// <summary>
        /// يولد كود SQL خام بناءً على سؤال المستخدم والسياق المسترجع.
        /// </summary>
        /// <param name="userQuestion">السؤال الأصلي للمستخدم</param>
        /// <param name="contextSnippets">"الحقائق" المسترجعة من Qdrant (من Layer 1)</param>
        /// <returns>كود SQL "خام" (غير موثوق به) تم توليده بواسطة الـ AI.</returns>
        Task<string> GenerateSqlAsync(string userQuestion, List<string> contextSnippets);
    }
}