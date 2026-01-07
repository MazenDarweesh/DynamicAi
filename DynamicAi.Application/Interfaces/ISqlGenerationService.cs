using DynamicAi.Application.Models; // (جديد)
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// (مُعدل - Layer 2)
    /// مسؤول عن بناء الـ Prompt وتوليد كود SQL "خام" (Raw)
    /// باستخدام السياق "الغني" (Rich Context) من Layer 1.
    /// </summary>
    public interface ISqlGenerationService
    {
        /// <summary>
        /// (مُعدل) يولد كود SQL خام بناءً على سؤال المستخدم والسياق "الغني".
        /// </summary>
        /// <param name="userQuestion">السؤال الأصلي للمستخدم (أو السؤال المكثف من Condenser)</param>
        /// <param name="contextBundle">"سلة السياق" الغنية (Schema, Data, Rules) من Layer 1</param>
        /// <param name="previousSql">كود SQL السابق (Optional). Used for wrapping in Subqueries.</param>
        /// <returns>كود SQL "خام" (غير موثوق به) تم توليده بواسطة الـ AI.</returns>
        Task<string> GenerateSqlAsync(string userQuestion, RetrievalContextBundle contextBundle, string? previousSql = null);
    }
}
