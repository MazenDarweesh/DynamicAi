using DynamicAi.Application.Models;
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
        /// <param name="actionType">
        /// نوع الإجراء المطلوب:
        /// - WrapPrevious: لف الـ SQL السابق في subquery وتطبيق التعديلات عليه.
        /// - Regenerate: توليد SQL جديد مع استخدام السياق السابق للفهم.
        /// - New: توليد SQL جديد تماماً.
        /// </param>
        /// <returns>Result containing SQL, confidence score, and optional clarification.</returns>
        Task<SqlGenerationResult> GenerateSqlAsync(
            string userQuestion,
            RetrievalContextBundle contextBundle,
            string? previousSql = null,
            QueryActionType actionType = QueryActionType.Regenerate);
    }
}
