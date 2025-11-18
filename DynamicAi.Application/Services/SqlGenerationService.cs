using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Text;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// (تنفيذ Layer 2 المُعدل)
    /// يستخدم موديل المحادثة (gemini-2.5-pro) والسياق الغني
    /// لتحويل سؤال المستخدم إلى SQL دقيق.
    /// </summary>
    public class SqlGenerationService : ISqlGenerationService
    {
        private readonly IChatCompletionService _chatService;

        public SqlGenerationService(IChatCompletionService chatService)
        {
            _chatService = chatService;
        }

        /// <summary>
        /// (مُعدل) يولد كود SQL خام بناءً على سؤال المستخدم والسياق "الغني".
        /// </summary>
        public async Task<string> GenerateSqlAsync(string userQuestion, RetrievalContextBundle contextBundle)
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
            Console.WriteLine($"[SqlGeneration] Context Bundle Received:");
            Console.WriteLine($"  - Schema Scripts: {contextBundle.SchemaScripts?.Count ?? 0}");
            Console.WriteLine($"  - Dictionary Entries: {contextBundle.DictionaryEntries?.Count ?? 0}");
            Console.WriteLine($"  - Business Rules: {contextBundle.BusinessRules?.Count ?? 0}");

            // ============================================================================
            // STEP 1: تحليل محتويات Context Bundle
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[SqlGeneration] STEP 1: Analyzing Context Bundle...");
            Console.ResetColor();

            if (contextBundle.SchemaScripts != null && contextBundle.SchemaScripts.Any())
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Schema Scripts available: {contextBundle.SchemaScripts.Count}");
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.Gray;
                foreach (var script in contextBundle.SchemaScripts.Take(2))
                {
                    var preview = script.Length > 100 ? script.Substring(0, 100) + "..." : script;
                    Console.WriteLine($"  Preview: {preview}");
                }
                if (contextBundle.SchemaScripts.Count > 2)
                {
                    Console.WriteLine($"  ... and {contextBundle.SchemaScripts.Count - 2} more");
                }
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[SqlGeneration] ⚠ No Schema Scripts provided");
                Console.ResetColor();
            }

            if (contextBundle.DictionaryEntries != null && contextBundle.DictionaryEntries.Any())
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Dictionary Entries available: {contextBundle.DictionaryEntries.Count}");
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.Gray;
                foreach (var entry in contextBundle.DictionaryEntries.Take(2))
                {
                    var preview = entry.Length > 100 ? entry.Substring(0, 100) + "..." : entry;
                    Console.WriteLine($"  Preview: {preview}");
                }
                if (contextBundle.DictionaryEntries.Count > 2)
                {
                    Console.WriteLine($"  ... and {contextBundle.DictionaryEntries.Count - 2} more");
                }
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[SqlGeneration] ⚠ No Dictionary Entries provided");
                Console.ResetColor();
            }

            if (contextBundle.BusinessRules != null && contextBundle.BusinessRules.Any())
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Business Rules available: {contextBundle.BusinessRules.Count}");
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.Gray;
                foreach (var rule in contextBundle.BusinessRules)
                {
                    Console.WriteLine($"  Rule: {rule}");
                }
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[SqlGeneration] ⚠ No Business Rules provided");
                Console.ResetColor();
            }

            // ============================================================================
            // STEP 2: بناء الـ Prompt الاحترافي
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[SqlGeneration] STEP 2: Building AI prompt with rich context...");
            Console.ResetColor();

            var promptStart = DateTime.UtcNow;
            var promptBuilder = new StringBuilder();
            
            promptBuilder.AppendLine("You are an expert SQL Server developer tasked with writing T-SQL queries for an ERP system.");
            promptBuilder.AppendLine("You must strictly follow all rules. Answer with T-SQL code only. Do not add any explanations.");

            promptBuilder.AppendLine("\n--- RULES ---");
            promptBuilder.AppendLine("1. ONLY use the Tables, Views, and Columns provided in the 'SCHEMA CONTEXT'.");
            promptBuilder.AppendLine("2. Use the 'DICTIONARY CONTEXT' to map user terms (Arabic) to the correct database ColumnNames (English).");
            promptBuilder.AppendLine("3. (CRITICAL) You MUST include the 'BUSINESS RULES' (TailConditions) in your WHERE clause.");
            promptBuilder.AppendLine("4. If the question cannot be answered with the given context, respond with 'ERROR: Insufficient context.'");

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("[SqlGeneration] Building prompt section: SCHEMA CONTEXT...");
            Console.ResetColor();
            promptBuilder.AppendLine("\n--- SCHEMA CONTEXT (The available Tables/Views) ---");
            if (contextBundle.SchemaScripts != null && contextBundle.SchemaScripts.Any())
            {
                int schemaIndex = 1;
                foreach (var script in contextBundle.SchemaScripts)
                {
                    promptBuilder.AppendLine($"\n[Schema Script {schemaIndex}]");
                    promptBuilder.AppendLine(script);
                    schemaIndex++;
                }
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Added {contextBundle.SchemaScripts.Count} schema script(s)");
                Console.ResetColor();
            }
            else
            {
                promptBuilder.AppendLine("(No schema context available)");
            }

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("[SqlGeneration] Building prompt section: DICTIONARY CONTEXT...");
            Console.ResetColor();
            promptBuilder.AppendLine("\n--- DICTIONARY CONTEXT (User Labels to DB Columns) ---");
            if (contextBundle.DictionaryEntries != null && contextBundle.DictionaryEntries.Any())
            {
                int dictIndex = 1;
                foreach (var entry in contextBundle.DictionaryEntries)
                {
                    promptBuilder.AppendLine($"\n[Dictionary Entry {dictIndex}]");
                    promptBuilder.AppendLine(entry);
                    dictIndex++;
                }
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Added {contextBundle.DictionaryEntries.Count} dictionary entry/entries");
                Console.ResetColor();
            }
            else
            {
                promptBuilder.AppendLine("(No dictionary context available)");
            }

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("[SqlGeneration] Building prompt section: BUSINESS RULES...");
            Console.ResetColor();
            promptBuilder.AppendLine("\n--- BUSINESS RULES (Must be included) ---");
            if (contextBundle.BusinessRules != null && contextBundle.BusinessRules.Any())
            {
                int ruleIndex = 1;
                foreach (var rule in contextBundle.BusinessRules)
                {
                    promptBuilder.AppendLine($"\n[Business Rule {ruleIndex}]");
                    promptBuilder.AppendLine(rule);
                    ruleIndex++;
                }
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Added {contextBundle.BusinessRules.Count} business rule(s)");
                Console.ResetColor();
            }
            else
            {
                promptBuilder.AppendLine("(No business rules available)");
            }

            promptBuilder.AppendLine("\n--- TASK ---");
            promptBuilder.AppendLine($"Q: {userQuestion}");
            promptBuilder.AppendLine("A: ");

            var fullPrompt = promptBuilder.ToString();
            var promptDuration = (DateTime.UtcNow - promptStart).TotalMilliseconds;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SqlGeneration] ✓ Prompt built successfully in {promptDuration:F0}ms");
            Console.WriteLine($"[SqlGeneration] Prompt size: {fullPrompt.Length} characters");
            Console.WriteLine($"[SqlGeneration] Prompt structure:");
            Console.WriteLine($"  - Rules: 4");
            Console.WriteLine($"  - Schema scripts: {contextBundle.SchemaScripts?.Count ?? 0}");
            Console.WriteLine($"  - Dictionary entries: {contextBundle.DictionaryEntries?.Count ?? 0}");
            Console.WriteLine($"  - Business rules: {contextBundle.BusinessRules?.Count ?? 0}");
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

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"[SqlGeneration] Chat history created with 1 user message");
            Console.WriteLine($"[SqlGeneration] Awaiting AI response...");
            Console.ResetColor();

            var aiStart = DateTime.UtcNow;
            var response = await _chatService.GetChatMessageContentAsync(chatHistory);
            var aiDuration = (DateTime.UtcNow - aiStart).TotalMilliseconds;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SqlGeneration] ✓ AI response received in {aiDuration:F0}ms");
            Console.ResetColor();

            Console.WriteLine($"[SqlGeneration] Raw AI response length: {response.Content?.Length ?? 0} characters");
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

            var extractStart = DateTime.UtcNow;
            var extractedSql = ExtractSql(response.Content);
            var extractDuration = (DateTime.UtcNow - extractStart).TotalMilliseconds;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SqlGeneration] ✓ SQL extracted and cleaned in {extractDuration:F0}ms");
            Console.WriteLine($"[SqlGeneration] Final SQL length: {extractedSql.Length} characters");
            Console.WriteLine($"[SqlGeneration] Final SQL:");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(extractedSql);
            Console.ResetColor();

            // ============================================================================
            // [LOGGING] نهاية عملية SQL Generation
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║            [Layer 2: SQL GENERATION] - COMPLETED ✓                     ║");
            Console.WriteLine($"║            Total Time: {(DateTime.UtcNow - (aiStart - TimeSpan.FromMilliseconds(aiDuration + promptDuration))).TotalMilliseconds:F0}ms                                               ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝\n");
            Console.ResetColor();

            return extractedSql;
        }

        private string ExtractSql(string content)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"[SqlGeneration] Cleaning: Starting SQL extraction...");
            Console.WriteLine($"[SqlGeneration] Cleaning: Input length: {content?.Length ?? 0} characters");
            Console.ResetColor();

            // (تنظيف بسيط لإزالة "```sql")
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

            var cleaned = content.Trim();
            
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"[SqlGeneration] Cleaning: Output length: {cleaned.Length} characters");
            Console.ResetColor();

            return cleaned;
        }
    }
}