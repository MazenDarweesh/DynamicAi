using System.Collections.Generic;
using System.Threading.Tasks;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// مسؤول عن توفير "أمثلة قليلة" (Few-Shot Examples)
    /// لتحسين دقة توليد الـ SQL.
    /// </summary>
    public interface IFewShotExampleService
    {
        /// <summary>
        /// (لـ MVP) يجلب قائمة ثابتة (Hard-coded) من الأمثلة.
        /// (للمستقبل) يمكن أن يبحث في Qdrant عن أمثلة مشابهة.
        /// </summary>
        /// <param name="userQuestion">سؤال المستخدم (للاستخدام المستقبلي)</param>
        /// <returns>قائمة من الأمثلة (سؤال وجواب SQL).</returns>
        Task<List<(string Question, string SqlAnswer)>> GetExamplesAsync(string userQuestion);
    }
}