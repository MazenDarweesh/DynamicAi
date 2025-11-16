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
            // ============================================================================
            // [LOGGING] بداية عملية SQL Generation
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║              [Layer 2: SQL GENERATION] - STARTING                      ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
            Console.ResetColor();

            Console.WriteLine($"[SqlGeneration] User Question: \"{userQuestion}\"");
            Console.WriteLine($"[SqlGeneration] Context Snippets: {contextSnippets?.Count ?? 0}");

            // ============================================================================
            // STEP 1: جلب Few-Shot Examples
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[SqlGeneration] STEP 1: Fetching few-shot examples...");
            Console.ResetColor();

            var examplesStart = DateTime.UtcNow;
            var examples = await _fewShotService.GetExamplesAsync(userQuestion);
            var examplesDuration = (DateTime.UtcNow - examplesStart).TotalMilliseconds;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SqlGeneration] ✓ Fetched {examples.Count} example(s) in {examplesDuration:F0}ms");
            Console.ResetColor();

            if (examples.Any())
            {
                Console.WriteLine($"[SqlGeneration] Examples preview:");
                foreach (var (q, a) in examples.Take(2))
                {
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine($"  Q: {q.Substring(0, Math.Min(60, q.Length))}...");
                    Console.WriteLine($"  A: {a.Substring(0, Math.Min(60, a.Length))}...");
                    Console.ResetColor();
                }
            }

            // ============================================================================
            // STEP 2: بناء الـ Prompt
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[SqlGeneration] STEP 2: Building AI prompt...");
            Console.ResetColor();

            var promptBuilder = new StringBuilder();
            promptBuilder.AppendLine("You are an expert SQL Server developer tasked with writing T-SQL queries for an ERP system.");
            promptBuilder.AppendLine("You must strictly follow all rules. Answer with T-SQL code only. Do not add any explanations.");

            promptBuilder.AppendLine("\n--- RULES ---");
            promptBuilder.AppendLine("1. ONLY use the Tables and Views provided in the 'CONTEXT' section.");
            promptBuilder.AppendLine("2. ONLY use the Columns provided in the 'CONTEXT' section.");
            promptBuilder.AppendLine("3. DO NOT use any table or column not explicitly listed in the 'CONTEXT'.");
            promptBuilder.AppendLine("4. If the question cannot be answered with the given context, respond with 'ERROR: Insufficient context.'");

            promptBuilder.AppendLine("\n--- CONTEXT (Available Schema and Data Facts) ---");
            if (contextSnippets != null && contextSnippets.Any())
            {
                int snippetIndex = 1;
                foreach (var snippet in contextSnippets)
                {
                    promptBuilder.AppendLine($"\n[Context Snippet {snippetIndex}]");
                    promptBuilder.AppendLine(snippet);
                    snippetIndex++;
                }
            }
            else
            {
                promptBuilder.AppendLine("(No context available)");
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

            var fullPrompt = promptBuilder.ToString();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SqlGeneration] ✓ Prompt built successfully");
            Console.WriteLine($"[SqlGeneration] Prompt size: {fullPrompt.Length} characters");
            Console.WriteLine($"[SqlGeneration] Prompt structure:");
            Console.WriteLine($"  - Rules: 4");
            Console.WriteLine($"  - Context snippets: {contextSnippets?.Count ?? 0}");
            Console.WriteLine($"  - Examples: {examples.Count}");
            Console.ResetColor();

            // عرض الـ Prompt الكامل (اختياري - يمكن تعطيله في Production)
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("\n[SqlGeneration] ═══ FULL PROMPT TO AI ═══");
            Console.WriteLine(fullPrompt);
            Console.WriteLine("[SqlGeneration] ═══ END OF PROMPT ═══\n");
            Console.ResetColor();

            // ============================================================================
            // STEP 3: استدعاء AI Model (Gemini)
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("[SqlGeneration] STEP 3: Sending request to Gemini AI...");
            Console.ResetColor();

            var chatHistory = new ChatHistory();
            chatHistory.AddUserMessage(fullPrompt);

            var aiStart = DateTime.UtcNow;
            var response = await _chatService.GetChatMessageContentAsync(chatHistory);
            var aiDuration = (DateTime.UtcNow - aiStart).TotalMilliseconds;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SqlGeneration] ✓ AI response received in {aiDuration:F0}ms");
            Console.ResetColor();

            Console.WriteLine($"[SqlGeneration] Raw AI response:");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{response.Content}");
            Console.ResetColor();

            // ============================================================================
            // STEP 4: استخراج وتنظيف SQL
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[SqlGeneration] STEP 4: Extracting and cleaning SQL...");
            Console.ResetColor();

            var extractedSql = ExtractSql(response.Content);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SqlGeneration] ✓ SQL extracted and cleaned");
            Console.WriteLine($"[SqlGeneration] Final SQL length: {extractedSql.Length} characters");
            Console.WriteLine($"[SqlGeneration] Final SQL:");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(extractedSql);
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║            [Layer 2: SQL GENERATION] - COMPLETED ✓                     ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝\n");
            Console.ResetColor();

            return extractedSql;
        }

        private string ExtractSql(string content)
        {
            // (تنظيف بسيط لإزالة "```sql" التي يضيفها الـ AI أحياناً)
            content = content.Trim();
            
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"[SqlGeneration] Cleaning: Checking for markdown code fences...");
            Console.ResetColor();

            if (content.StartsWith("```sql", StringComparison.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"[SqlGeneration] Cleaning: Removing '```sql' prefix");
                Console.ResetColor();
                content = content.Substring(6);
            }
            else if (content.StartsWith("```"))
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"[SqlGeneration] Cleaning: Removing '```' prefix");
                Console.ResetColor();
                content = content.Substring(3);
            }

            if (content.EndsWith("```"))
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"[SqlGeneration] Cleaning: Removing '```' suffix");
                Console.ResetColor();
                content = content.Substring(0, content.Length - 3);
            }

            return content.Trim();
        }
    }
}