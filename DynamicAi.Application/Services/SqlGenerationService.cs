using DynamicAi.Application.Interfaces;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Text;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// (تنفيذ Layer 2)
    /// يستخدم موديل المحادثة (gemini-2.5-pro) والأمثلة (Few-Shots)
    /// لتحويل سؤال المستخدم إلى SQL.
    /// </summary>
    public class SqlGenerationService : ISqlGenerationService
    {
        private readonly IChatCompletionService _chatService;
        private readonly IFewShotExampleService _fewShotService;

        public SqlGenerationService(
            IChatCompletionService chatService,
            IFewShotExampleService fewShotService)
        {
            _chatService = chatService;
            _fewShotService = fewShotService;
        }

        /// <summary>
        /// يولد كود SQL خام بناءً على سؤال المستخدم والسياق المسترجع.
        /// </summary>
        public async Task<string> GenerateSqlAsync(string userQuestion, List<string> contextSnippets)
        {
            // 1. جلب الأمثلة (Few-Shots)
            var examples = await _fewShotService.GetExamplesAsync(userQuestion);

            // 2. بناء الـ Prompt الذكي (Smart Prompt)
            var promptBuilder = new StringBuilder();
            promptBuilder.AppendLine("You are an expert SQL Server developer tasked with writing T-SQL queries for an ERP system.");
            promptBuilder.AppendLine("You must strictly follow all rules. Answer with T-SQL code only. Do not add any explanations.");

            promptBuilder.AppendLine("\n--- RULES ---");
            promptBuilder.AppendLine("1. ONLY use the Tables and Views provided in the 'CONTEXT' section.");
            promptBuilder.AppendLine("2. ONLY use the Columns provided in the 'CONTEXT' section.");
            promptBuilder.AppendLine("3. DO NOT use any table or column not explicitly listed in the 'CONTEXT'.");
            promptBuilder.AppendLine("4. If the question cannot be answered with the given context, respond with 'ERROR: Insufficient context.'");

            promptBuilder.AppendLine("\n--- CONTEXT (Available Schema and Data Facts) ---");
            foreach (var snippet in contextSnippets)
            {
                promptBuilder.AppendLine(snippet);
            }

            promptBuilder.AppendLine("\n--- EXAMPLES (Follow this pattern) ---");
            foreach (var (q, a) in examples)
            {
                promptBuilder.AppendLine($"Q: {q}");
                promptBuilder.AppendLine($"A: {a}");
            }

            promptBuilder.AppendLine("\n--- TASK ---");
            promptBuilder.AppendLine($"Q: {userQuestion}");
            promptBuilder.AppendLine("A: ");

            // 3. استدعاء الـ AI (gemini-2.5-pro)
            var chatHistory = new ChatHistory();
            chatHistory.AddUserMessage(promptBuilder.ToString());

            var response = await _chatService.GetChatMessageContentAsync(chatHistory);

            // 4. استخراج الـ SQL "الخام"
            return ExtractSql(response.Content);
        }

        private string ExtractSql(string content)
        {
            // (تنظيف بسيط لإزالة "```sql" التي يضيفها الـ AI أحياناً)
            content = content.Trim();
            if (content.StartsWith("```sql"))
            {
                content = content.Substring(5);
            }
            if (content.EndsWith("```"))
            {
                content = content.Substring(0, content.Length - 3);
            }
            return content.Trim();
        }
    }
}