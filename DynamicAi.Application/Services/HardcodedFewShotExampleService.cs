using DynamicAi.Application.Interfaces;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// (تنفيذ MVP بسيط)
    /// يُرجع قائمة ثابتة (Hard-coded) من الأمثلة عالية الجودة
    /// لتدريب الـ LLM على كيفية الاستعلام من الـ Views الخاصة بنا.
    /// </summary>
    public class HardcodedFewShotExampleService : IFewShotExampleService
    {
        private readonly List<(string Question, string SqlAnswer)> _examples;

        public HardcodedFewShotExampleService()
        {
            // (هام: هذه الأمثلة يجب أن تكون دقيقة 100% ومطابقة للسكيما)
            _examples = new List<(string Question, string SqlAnswer)>
            {
                ("ما هي مبيعاتي الإجمالية الشهر الماضي؟",
                 "SELECT SUM(ContractAmountwTax) FROM ContractViewWithPaid WHERE ContractDate BETWEEN '...' AND '...'"),

                ("اعرض لي العقود الخاصة بالعميل 'شركة ABC'",
                 "SELECT ContractID, ContractDate, ContractAmountwTax FROM ContractViewWithPaid WHERE CustomerName = 'شركة ABC'"),

                ("كم عدد الأصناف في 'salessystemcontract'؟",
                 "SELECT COUNT(SystemID) FROM salessystemcontract")

                // (سنضيف المزيد من الأمثلة عالية الجودة هنا)
            };
        }

        /// <summary>
        /// يُرجع ببساطة القائمة الثابتة (يتجاهل السؤال في الـ MVP).
        /// </summary>
        public Task<List<(string Question, string SqlAnswer)>> GetExamplesAsync(string userQuestion)
        {
            return Task.FromResult(_examples);
        }
    }
}