using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel;
using System.Text;
using System.Text.RegularExpressions;

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

        // ============================================================================
        // GEMINI IMPLICIT CACHE OPTIMIZATION
        // Static rules are cached as a constant string to ensure IDENTICAL prefix
        // for every request, maximizing Gemini's implicit cache hit rate (75% discount)
        // ============================================================================
        private static readonly Lazy<string> _cachedStaticRules = new Lazy<string>(() => BuildStaticSystemRules());
        private static int _staticRulesTokenCount = 0;

        public SqlGenerationService(IChatCompletionService chatService)
        {
            _chatService = chatService;
        }

        /// <summary>
        /// Gets the cached static system rules (built once, reused for all requests)
        /// </summary>
        public static string CachedStaticRules => _cachedStaticRules.Value;

        /// <summary>
        /// Gets the estimated token count for static rules
        /// </summary>
        public static int StaticRulesTokenCount => _staticRulesTokenCount;

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
            Console.WriteLine($"  - Unified Context: {(!string.IsNullOrEmpty(contextBundle.UnifiedPageContext))}");
            Console.WriteLine($"  - Dictionary Entries: {contextBundle.DictionaryEntries?.Count ?? 0}");
            Console.WriteLine($"  - Business Rules: {contextBundle.BusinessRules?.Count ?? 0}");

            // ============================================================================ 
            // STEP 1: تحليل محتويات Context Bundle
            // ============================================================================ 
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[SqlGeneration] STEP 1: Analyzing Context Bundle...");
            Console.ResetColor();

            if (!string.IsNullOrEmpty(contextBundle.UnifiedPageContext))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Unified Page Context available.");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[SqlGeneration] ⚠ No Unified Page Context provided");
                Console.ResetColor();
            }

            if (contextBundle.DictionaryEntries != null && contextBundle.DictionaryEntries.Any())
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Dictionary Entries available: {contextBundle.DictionaryEntries.Count}");
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

            // ============================================================================
            // GEMINI IMPLICIT CACHE OPTIMIZATION
            // Use pre-built static rules (cached once, reused for all requests)
            // This ensures IDENTICAL prefix for every request, maximizing cache hit rate
            // ============================================================================
            promptBuilder.Append(CachedStaticRules);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SqlGeneration] ✓ Static rules loaded from cache ({StaticRulesTokenCount} tokens, ~{CachedStaticRules.Length} chars)");
            Console.WriteLine($"[SqlGeneration] ✓ Gemini Implicit Cache: Same prefix for all requests guarantees cache HIT");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("[SqlGeneration] Building prompt section: HIERARCHY & SCHEMA CONTEXT...");
            Console.ResetColor();
            promptBuilder.AppendLine("\n--- HIERARCHY & SCHEMA CONTEXT (The available Page structure and its Views) ---");
            if (!string.IsNullOrEmpty(contextBundle.UnifiedPageContext))
            {
                promptBuilder.AppendLine("This JSON object represents the complete page structure. The `SchemaScript` property contains the T-SQL CREATE VIEW statement.");
                promptBuilder.AppendLine(contextBundle.UnifiedPageContext);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Added Unified Page Context.");
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

            // Unit 2: Compact Dictionary Format - Add header explaining the format
            promptBuilder.AppendLine("Format: [Index] SearchName | ColumnName | InsertType | arColumnLabel | enColumnLabel | DroModel:DroValue:DroText (if dropdown)");
            promptBuilder.AppendLine("---");

            if (contextBundle.DictionaryEntries != null && contextBundle.DictionaryEntries.Any())
            {
                int dictIndex = 1;
                foreach (var entry in contextBundle.DictionaryEntries)
                {
                    // Parse JSON and convert to compact format
                    try
                    {
                        var node = System.Text.Json.JsonDocument.Parse(entry);
                        var root = node.RootElement;

                        var searchName = root.TryGetProperty("SearchName", out var sn) ? sn.GetString() ?? "" : "";
                        var columnName = root.TryGetProperty("ColumnName", out var cn) ? cn.GetString() ?? "" : "";
                        var insertType = root.TryGetProperty("InsertType", out var it) ? it.GetString() ?? "" : "";
                        var arLabel = root.TryGetProperty("arColumnLabel", out var ar) ? ar.GetString() ?? "" : "";
                        var enLabel = root.TryGetProperty("enColumnLabel", out var en) ? en.GetString() ?? "" : "";

                        // Build compact entry
                        var compactEntry = $"[{dictIndex}] {searchName} | {columnName} | {insertType} | {arLabel} | {enLabel}";

                        // Add dropdown info if applicable
                        if (insertType == "dropdown")
                        {
                            var droModel = root.TryGetProperty("DroModel", out var dm) ? dm.GetString() ?? "" : "";
                            var droValue = root.TryGetProperty("DroValue", out var dv) ? dv.GetString() ?? "" : "";
                            var droText = root.TryGetProperty("DroText", out var dt) ? dt.GetString() ?? "" : "";

                            if (!string.IsNullOrEmpty(droModel))
                            {
                                compactEntry += $" | {droModel}:{droValue}:{droText}";
                            }
                        }

                        promptBuilder.AppendLine(compactEntry);
                    }
                    catch
                    {
                        // Fallback: use original entry if parsing fails
                        promptBuilder.AppendLine($"[{dictIndex}] {entry}");
                    }
                    dictIndex++;
                }
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Added {contextBundle.DictionaryEntries.Count} dictionary entry/entries (COMPACT FORMAT)");
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


            if (!string.IsNullOrEmpty(contextBundle.ConversationHistoryContext))
            {
                promptBuilder.AppendLine("\n--- CONVERSATION HISTORY (PREVIOUS USER/AI TURN) ---");
                promptBuilder.AppendLine(contextBundle.ConversationHistoryContext);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SqlGeneration] ✓ Added Conversation History to prompt");
                Console.ResetColor();

                // Instruction to AI (Crucial for Option 3)
                promptBuilder.AppendLine("\n**INSTRUCTION FOR HISTORY:**");
                promptBuilder.AppendLine("- The user is asking a Follow-Up question based on the history above.");
                promptBuilder.AppendLine("- You MUST start with the 'Last Generated SQL' (if valid) and apply the new constraints.");
                promptBuilder.AppendLine("- If the user says 'filter by X', ADD a WHERE clause to the previous SQL.");
                promptBuilder.AppendLine("- If the user says 'sort by X', UPDATE the ORDER BY clause.");
            }

            promptBuilder.AppendLine("\n--- TASK ---");
            promptBuilder.AppendLine($"Q: {userQuestion}");
            promptBuilder.AppendLine("A: ");

            var fullPrompt = promptBuilder.ToString();
            var promptDuration = (DateTime.UtcNow - promptStart).TotalMilliseconds;

            // ============================================================================
            // UNIT 3: TOKEN METRICS LOGGING - Detailed breakdown of prompt components
            // ============================================================================
            int staticRulesSize = fullPrompt.IndexOf("--- HIERARCHY & SCHEMA CONTEXT") > 0
                ? fullPrompt.IndexOf("--- HIERARCHY & SCHEMA CONTEXT")
                : 0;
            int unifiedContextSize = contextBundle.UnifiedPageContext?.Length ?? 0;
            int dictionarySize = fullPrompt.Contains("--- DICTIONARY CONTEXT")
                ? (fullPrompt.IndexOf("--- BUSINESS RULES") - fullPrompt.IndexOf("--- DICTIONARY CONTEXT"))
                : 0;
            int businessRulesSize = 0;
            if (contextBundle.BusinessRules != null)
            {
                businessRulesSize = contextBundle.BusinessRules.Sum(r => r?.Length ?? 0) + (contextBundle.BusinessRules.Count * 30); // overhead
            }
            int userQuestionSize = userQuestion.Length + 20; // "Q: " + "A: " overhead

            int totalChars = fullPrompt.Length;
            int estimatedTokens = totalChars / 4; // ~4 chars per token (approximate)
            double estimatedInputCost = (estimatedTokens / 1_000_000.0) * 0.075; // Gemini Flash input: $0.075/1M tokens


            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n[TokenMetrics] ═══════════════ PROMPT BREAKDOWN ═══════════════");
            Console.WriteLine($"[TokenMetrics]   Static Rules (System):      {staticRulesSize,8:N0} chars (~{staticRulesSize / 4,6:N0} tokens)");
            Console.WriteLine($"[TokenMetrics]   Unified Context (Schema):   {unifiedContextSize,8:N0} chars (~{unifiedContextSize / 4,6:N0} tokens)");
            Console.WriteLine($"[TokenMetrics]   Dictionary (Compact):       {dictionarySize,8:N0} chars (~{dictionarySize / 4,6:N0} tokens)");
            Console.WriteLine($"[TokenMetrics]   Business Rules:             {businessRulesSize,8:N0} chars (~{businessRulesSize / 4,6:N0} tokens)");
            Console.WriteLine($"[TokenMetrics]   User Question:              {userQuestionSize,8:N0} chars (~{userQuestionSize / 4,6:N0} tokens)");
            Console.WriteLine($"[TokenMetrics]   ─────────────────────────────────────────────────────");
            Console.WriteLine($"[TokenMetrics]   TOTAL PROMPT:               {totalChars,8:N0} chars (~{estimatedTokens,6:N0} tokens)");
            Console.WriteLine($"[TokenMetrics]   Est. Input Cost:            ${estimatedInputCost:F6}");
            Console.WriteLine($"[TokenMetrics] ════════════════════════════════════════════════════════");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SqlGeneration] ✓ Prompt built successfully in {promptDuration:F0}ms");
            Console.WriteLine($"[SqlGeneration] Prompt size: {fullPrompt.Length} characters (~{estimatedTokens} tokens)");
            Console.WriteLine($"[SqlGeneration] Prompt structure:");
            Console.WriteLine($"  - System Cycle Rules: 4");
            Console.WriteLine($"  - Interactivity & Safety Rules: 3");
            Console.WriteLine($"  - T-SQL Syntax Rules: 5");
            Console.WriteLine($"  - Unified Context: {(!string.IsNullOrEmpty(contextBundle.UnifiedPageContext))}");
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

            // (New) Enforce Consistency
            var executionSettings = new PromptExecutionSettings
            {
                ExtensionData = new Dictionary<string, object>
                {
                    { "temperature", 0.0 }, // Zero temperature for deterministic output
                    { "topP", 0.1 }         // Low topP for more focused sampling
                }
            };

            var response = await _chatService.GetChatMessageContentAsync(chatHistory, executionSettings);
            var aiDuration = (DateTime.UtcNow - aiStart).TotalMilliseconds;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SqlGeneration] ✓ AI response received in {aiDuration:F0}ms");
            Console.ResetColor();

            // ============================================================================
            // GEMINI IMPLICIT CACHE DETECTION - Check for cached tokens in response
            // ============================================================================
            int cachedTokens = 0;
            int promptTokens = estimatedTokens; // From our earlier estimation
            int completionTokens = (response.Content?.Length ?? 0) / 4;

            // Try to extract cache info from response metadata
            if (response.Metadata != null)
            {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"\n[GeminiCache] ═══ RESPONSE METADATA ═══");
                foreach (var kvp in response.Metadata)
                {
                    Console.WriteLine($"[GeminiCache]   {kvp.Key}: {kvp.Value}");

                    // Look for cached token count in various possible keys
                    if (kvp.Key.ToLower().Contains("cached") || kvp.Key.ToLower().Contains("cache"))
                    {
                        if (int.TryParse(kvp.Value?.ToString(), out int cached))
                        {
                            cachedTokens = cached;
                        }
                    }
                    // Also check for usage metadata
                    if (kvp.Key.ToLower().Contains("usage") || kvp.Key.ToLower().Contains("token"))
                    {
                        Console.WriteLine($"[GeminiCache]   → Token-related: {kvp.Key} = {kvp.Value}");
                    }
                }
                Console.ResetColor();
            }

            // Calculate costs with cache consideration
            double inputCostPerMillion = 0.075;  // Gemini Flash: $0.075/1M input tokens
            double cachedCostPerMillion = 0.01875; // Cached tokens: 75% discount = $0.01875/1M
            double outputCostPerMillion = 0.30;   // Gemini Flash: $0.30/1M output tokens

            int uncachedTokens = promptTokens - cachedTokens;
            double inputCost = (uncachedTokens / 1_000_000.0) * inputCostPerMillion;
            double cachedCost = (cachedTokens / 1_000_000.0) * cachedCostPerMillion;
            double outputCost = (completionTokens / 1_000_000.0) * outputCostPerMillion;
            double totalCost = inputCost + cachedCost + outputCost;

            // Display cache status and costs
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n[GeminiCache] ═══════════════ CACHE & COST ANALYSIS ═══════════════");
            if (cachedTokens > 0)
            {
                double savings = ((cachedTokens / 1_000_000.0) * inputCostPerMillion) - cachedCost;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[GeminiCache] ✓ CACHE HIT! {cachedTokens:N0} tokens served from cache");
                Console.WriteLine($"[GeminiCache]   Cached Tokens:     {cachedTokens,8:N0} (75% discount)");
                Console.WriteLine($"[GeminiCache]   Uncached Tokens:   {uncachedTokens,8:N0} (full price)");
                Console.WriteLine($"[GeminiCache]   Savings:           ${savings:F6}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[GeminiCache] ○ No cache hit detected (first request or prefix changed)");
                Console.WriteLine($"[GeminiCache]   Note: Implicit caching will cache this prefix for future requests");
            }
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[GeminiCache]   ─────────────────────────────────────────────────────");
            Console.WriteLine($"[GeminiCache]   Input Cost:        ${inputCost:F6} ({uncachedTokens:N0} tokens)");
            Console.WriteLine($"[GeminiCache]   Cached Cost:       ${cachedCost:F6} ({cachedTokens:N0} tokens)");
            Console.WriteLine($"[GeminiCache]   Output Cost:       ${outputCost:F6} ({completionTokens:N0} tokens)");
            Console.WriteLine($"[GeminiCache]   ─────────────────────────────────────────────────────");
            Console.WriteLine($"[GeminiCache]   TOTAL COST:        ${totalCost:F6}");
            Console.WriteLine($"[GeminiCache] ════════════════════════════════════════════════════════");
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

        private string ExtractSql(string? content)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"[SqlGeneration] Cleaning: Starting SQL extraction...");
            Console.WriteLine($"[SqlGeneration] Cleaning: Input length: {content?.Length ?? 0} characters");
            Console.ResetColor();

            if (string.IsNullOrWhiteSpace(content)) return string.Empty;

            content = content.Trim();

            // Regex to match markdown code blocks with optional language identifier
            // Matches ``` followed by optional text (like 'sql', 'tsql'), then whitespace/newline
            var match = System.Text.RegularExpressions.Regex.Match(content, @"^```\w*\s+");
            if (match.Success)
            {
                Console.WriteLine($"[SqlGeneration] Cleaning: Removing markdown start fence '{match.Value.Trim()}'");
                Console.ResetColor();
                content = content.Substring(match.Length);
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

        /// <summary>
        /// Builds all static system rules once and caches them for reuse.
        /// This maximizes Gemini's implicit caching by ensuring identical prefix every request.
        /// </summary>
        private static string BuildStaticSystemRules()
        {
            Console.WriteLine("🔄 [Cache] Building Static Rules - Version 4.0");
            var sb = new StringBuilder();

            // 1. Persona
            sb.AppendLine("You are an expert ERP Database Architect specialized in a specific Dynamic AI system.");
            sb.AppendLine("Your goal is to map user questions to a T-SQL query strictly following the system's metadata cycle.");
            sb.AppendLine("You MUST strictly follow all rules. If you generate SQL, output only the T-SQL code. Do not add any explanations.");

            // 2. Core Rules
            sb.AppendLine("\n--- CORE RULES (CRITICAL) ---");
            sb.AppendLine("1. **🛑 SELECT ONLY - READ-ONLY SYSTEM (ABSOLUTE SECURITY RULE)**:");
            sb.AppendLine("   ⚠️ THIS IS THE HIGHEST PRIORITY RULE - CANNOT BE OVERRIDDEN");
            sb.AppendLine("   - You are STRICTLY LIMITED to generating **SELECT statements ONLY**.");
            sb.AppendLine("   - **FORBIDDEN COMMANDS (NEVER GENERATE THESE)**:");
            sb.AppendLine("     * INSERT, UPDATE, DELETE, MERGE");
            sb.AppendLine("     * DROP, TRUNCATE, ALTER, CREATE");
            sb.AppendLine("     * EXEC, EXECUTE, sp_, xp_");
            sb.AppendLine("     * GRANT, REVOKE, DENY");
            sb.AppendLine("     * Any DDL (Data Definition Language) commands");
            sb.AppendLine("     * Any DML (Data Manipulation Language) commands except SELECT");
            sb.AppendLine("   - **IF USER REQUESTS ANY FORBIDDEN COMMAND**:");
            sb.AppendLine("     * DO NOT generate the SQL");
            sb.AppendLine("     * Respond ONLY with: `REFUSED: عذراً، هذا النظام مخصص للاستعلامات فقط (SELECT). لا يمكنني تنفيذ أوامر تعديل أو حذف أو إضافة البيانات. يرجى التواصل مع مسؤول النظام للعمليات الأخرى.`");
            sb.AppendLine("   - **EXAMPLES OF REFUSED REQUESTS**:");
            sb.AppendLine("     * 'احذف الفاتورة رقم 5' → REFUSED");
            sb.AppendLine("     * 'عدل اسم العميل' → REFUSED");
            sb.AppendLine("     * 'أضف فاتورة جديدة' → REFUSED");
            sb.AppendLine("     * 'امسح كل البيانات' → REFUSED");
            sb.AppendLine("");

            sb.AppendLine("2. **STRICT TABLE WHITELIST (DATA SOURCES)**: You are STRICTLY LIMITED to using tables/views from these specific metadata fields ONLY:");
            sb.AppendLine("   - A) `TableName` from the `PageAttribute` (The Master View).");
            sb.AppendLine("   - B) `TableName` from the `PageDetail` (The Detail View).");
            sb.AppendLine("   - C) `DroModel` from `ListSetups` (Lookup Tables), BUT ONLY when resolving a column where `InsertType` == 'dropdown'.");
            sb.AppendLine("   - **PROHIBITION**: Using any other table/view (even if found in SchemaScript) that is NOT referenced in these three fields is a HALLUCINATION and is FORBIDDEN.");
            sb.AppendLine("2. **MANDATORY SEARCH PATH**: To find a column, you MUST ONLY search in the 'DICTIONARY CONTEXT' (ListSetups).");
            sb.AppendLine("   - **Step A**: Identify the `ListName` from the Master View (`PageAttribute`) or Details View (`PageDetail`).");
            sb.AppendLine("   - **Step B**: Search ONLY within the `ListSetups` entries linked to that `ListName`.");
            sb.AppendLine("   - **Step C**: Match the user's Arabic/English word to `arColumnLabel` or `enColumnLabel` to find the mapping.");

            // Response Format Rules
            sb.AppendLine("\n--- RESPONSE FORMAT RULES (STRICT) ---");
            sb.AppendLine("1. **NO HYBRID RESPONSES**: You must output EITHER valid T-SQL code OR a clarification message. NEVER BOTH.");
            sb.AppendLine("2. **IF CLARIFICATION NEEDED**: Output ONLY `CLARIFICATION_NEEDED: [Message in Arabic]`. Do NOT write any SQL after it.");
            sb.AppendLine("3. **IF SQL GENERATED**: Output ONLY the SQL code inside markdown tags.");

            // System Cycle
            sb.AppendLine("\n--- THE SYSTEM CYCLE (MENTAL MODEL) ---");
            sb.AppendLine("1. **Identify Page**: First, determine which 'Page' the user is asking about from the HIERARCHY CONTEXT.");
            sb.AppendLine("2. **Master View (PageAttribute)**: The page's main data source is defined in the HIERARCHY CONTEXT (PageAttribute.TableName). This is the primary FROM table.");
            sb.AppendLine("3. **Details View (PageDetail)**: If the user asks for item details (e.g., Product Name, Item Quantity), you MUST use an INNER JOIN to the detail view/table (PageDetail.TableName) using the appropriate key.");
            sb.AppendLine("4. **MANDATORY COLUMN MAPPING**: The ONLY source for translating the user's requested field (Arabic/English) to a DB Column Name is the 'DICTIONARY CONTEXT' (ListSetups). **NEVER GUESS** a column name. Match the user's word to `arColumnLabel` or `enColumnLabel` to find the correct `ColumnName` or `DroText` mapping.");

            // Interactivity & Safety
            sb.AppendLine("\n--- INTERACTIVITY & SAFETY RULES (CRITICAL) ---");
            sb.AppendLine("1. **NO HALLUCINATION**: You MUST only use Tables/Views/Columns explicitly provided in the HIERARCHY and DICTIONARY CONTEXTS. Inventorying data is strictly forbidden.");
            sb.AppendLine("2. **MANDATORY FILTER**: You MUST include the 'BUSINESS RULES' (TailConditions) in your WHERE clause.");
            sb.AppendLine("3. **NO HALLUCINATION**: If the user asks for a field, and you cannot find a matching `arColumnLabel` or `enColumnLabel` in the DICTIONARY CONTEXT, the field DOES NOT EXIST. Do not invent columns like 'AreaName' or 'SystemID' if they are not explicitly linked via the ListSetups provided.");
            sb.AppendLine("4. **ALIASING RULE**: You MUST use the `arColumnLabel` from the ListSetups entry as the final SQL column alias (e.g., `SELECT GovName AS [الحي]`).");

            // Company Filter
            sb.AppendLine("\n3. **MANDATORY COMPANY FILTER (CRITICAL - NEVER FORGET)**:");
            sb.AppendLine("   ⚠️ EXTREMELY IMPORTANT - Multi-Tenant Security:");
            sb.AppendLine("   - EVERY query MUST include: `AND ComID = @CurrentCompanyId` in the WHERE clause");
            sb.AppendLine("   - This is a SECURITY requirement to isolate data by company");
            sb.AppendLine("   - NEVER skip this condition, even for aggregations (SUM, COUNT, AVG)");
            sb.AppendLine("   - The parameter `@CurrentCompanyId` will be provided by the system at runtime");
            sb.AppendLine("");
            sb.AppendLine("   **CORRECT Examples**:");
            sb.AppendLine("   ✓ `SELECT SUM(Total) FROM Sales WHERE MONTH(Date) = 9 AND ComID = @CurrentCompanyId`");
            sb.AppendLine("   ✓ `SELECT * FROM Contracts WHERE ContractID = 123 AND ComID = @CurrentCompanyId`");
            sb.AppendLine("   ✓ `SELECT TOP 5 * FROM Invoices WHERE ... (Business Rules) ... AND ComID = @CurrentCompanyId`");
            sb.AppendLine("");
            sb.AppendLine("   **WRONG Examples (NEVER DO THIS)**:");
            sb.AppendLine("   ✗ `SELECT SUM(Total) FROM Sales WHERE MONTH(Date) = 9` (Missing ComID - SECURITY BREACH!)");
            sb.AppendLine("   ✗ `SELECT * FROM Contracts WHERE ContractID = 123` (Missing ComID - SECURITY BREACH!)");

            // T-SQL Syntax Rules
            sb.AppendLine("\n--- T-SQL SYNTAX RULES ---");
            sb.AppendLine("1. DATE LOGIC: If a month is mentioned without a year: Use T-SQL: `WHERE MONTH(DateCol) = X`. Do NOT assume a specific year unless specified.");
            sb.AppendLine("   - If needing a range, calculate relative to GETDATE().");
            sb.AppendLine("2. TEXT SEARCH: For names/descriptions, NEVER use '='. ALWAYS use `LIKE N'%Value%'`.");
            sb.AppendLine("   - IMPORTANT: You MUST prefix string literals with 'N' to support Arabic characters (e.g., `N'أحمد'`).");
            sb.AppendLine("3. MATH SAFETY: When performing calculations (SUM, AVG, +, -), ALWAYS wrap columns with `ISNULL(Column, 0)`.");
            sb.AppendLine("   - Example: `SUM(ISNULL(PaidAmount, 0))` instead of `SUM(PaidAmount)`.");
            sb.AppendLine("4. OUTPUT FORMAT: Use descriptive ARABIC aliases for columns.");
            sb.AppendLine("   - Example: `SELECT Sum(Total) AS [اجمالي المبيعات]`. Use terms from 'DICTIONARY CONTEXT' for aliases.");
            sb.AppendLine("5. SAFETY: If no aggregation (SUM/COUNT) is requested, ALWAYS add `TOP 100` to the query.");
            sb.AppendLine("6. **STRICT ALIASING (PREVENT AMBIGUITY)**: You MUST assign an alias to every table (e.g., `FROM Table AS T1`). You MUST prefix EVERY column in the `SELECT`, `WHERE`, `JOIN`, and `ORDER BY` clauses with its alias (e.g., `T1.IsResale`, `T2.ContractType`). NEVER use a column name without its alias.");

            // More T-SQL Rules
            sb.AppendLine("\n--- T-SQL SYNTAX RULES ---");
            sb.AppendLine("1. **STRICT ALIASING**: Use aliases (e.g., `T1.Column`).");
            sb.AppendLine("2. **NO CTEs ALLOWED**: You are FORBIDDEN from using Common Table Expressions (`WITH ...`).");
            sb.AppendLine("   - USE ONLY standard `SELECT`, `JOIN`, and `SUBQUERIES` in the `WHERE` clause.");
            sb.AppendLine("   - REASON: Performance and security constraints.");
            sb.AppendLine("3. **DATE LOGIC**: Use `WHERE MONTH(Col) = X`.");
            sb.AppendLine("4. **SEARCH**: Use `LIKE N'%Val%'`.");
            sb.AppendLine("5. **LIMITS**: Use `TOP 100`.");

            // Default Ordering
            sb.AppendLine("\n6. **DEFAULT ORDERING (MANDATORY when no ORDER BY specified by user)**:");
            sb.AppendLine("   - If the user does NOT explicitly request a specific order/sort, you MUST add ORDER BY clause automatically.");
            sb.AppendLine("   - **⚠️ IMPORTANT NOTE**: The column names below (DocNo, DocDate, etc.) are EXAMPLES ONLY to help you understand the CONCEPT.");
            sb.AppendLine("     You MUST determine the ACTUAL column name from the HIERARCHY CONTEXT and DICTIONARY CONTEXT provided.");
            sb.AppendLine("     Look for columns with ID/key/Auto patterns, date/datetime types in the schema and ListSetups.");
            sb.AppendLine("   - **ORDER PRIORITY (Descending - Newest/Largest First)**:");
            sb.AppendLine("     * **Priority 1**: If a Document Number/ID column exists (e.g., DocNo, InvoiceNo, RecordID, ID) → ORDER BY [ActualDocNoColumn] DESC");
            sb.AppendLine("     * **Priority 2**: If no Doc Number but a Date/DateTime column exists → ORDER BY [ActualDateColumn] DESC");
            sb.AppendLine("     * **Priority 3**: If neither, use numeric column → ORDER BY [ActualNumericColumn] DESC");
            sb.AppendLine("     * **Priority 4**: If no numeric column, use alphabetical order on text column → ORDER BY [ActualTextColumn] DESC");
            sb.AppendLine("   - **RATIONALE**: Users typically want to see the highest document numbers (latest invoices) first.");
            sb.AppendLine("   - **KEYWORDS indicating user wants specific order (DO NOT apply default)**:");
            sb.AppendLine("     * Arabic: 'رتب', 'ترتيب', 'من الأصغر', 'من الأكبر', 'تصاعدي', 'تنازلي', 'الأول', 'الأخير'");
            sb.AppendLine("     * English: 'order by', 'sort', 'ascending', 'descending', 'first', 'last', 'oldest', 'newest'");

            // Dynamic Schema Intelligence
            sb.AppendLine("\n--- DYNAMIC SCHEMA INTELLIGENCE (METADATA LOGIC) ---");
            sb.AppendLine("You must interpret the provided Context JSON to understand relationships and column definitions dynamically. Do NOT rely on guessed column names.");
            sb.AppendLine("\n1. **MASTER-DETAIL RELATIONSHIP LOGIC**:");
            sb.AppendLine("   To join a Master View (PageAttribute) with its Details (PageDetail), follow these dynamic linking keys:");
            sb.AppendLine("   - **Master Key**: Look for the `Primary` property in the `PageAttribute` context. This is the PK of the Master View.");
            sb.AppendLine("   - **Detail Key**: Look for the `ForeignKey` property in the `PageDetail` context. This is the FK in the Detail View.");
            sb.AppendLine("   - **JOIN Rule**: `ON MasterView.[Primary_Column] = DetailView.[ForeignKey_Column]`.");

            // Column Resolution Algorithm
            sb.AppendLine("\n--- COLUMN RESOLUTION ALGORITHM (EXECUTE MENTALLY BEFORE WRITING SQL) ---");
            sb.AppendLine("You are FORBIDDEN from guessing column names. You MUST follow this strict 4-step algorithm for EVERY column requested:");
            sb.AppendLine("\n**STEP 1: SEARCH (The Lookup)**");
            sb.AppendLine("- Take the user's Arabic word (e.g., 'الحي').");
            sb.AppendLine("- SCAN the 'DICTIONARY CONTEXT' (ListSetups) to find an entry where `arColumnLabel` MATCHES 'الحي'.");
            sb.AppendLine("- IF NOT FOUND -> RETURN `CLARIFICATION_NEEDED: Cannot find column 'الحي'`. DO NOT GUESS.");
            sb.AppendLine("\n**STEP 2: IDENTIFY METADATA**");
            sb.AppendLine("- Once found, extract these exact values from the entry:");
            sb.AppendLine("  * `SearchName`: (The FK column in current view, e.g., 'Gov')");
            sb.AppendLine("  * `ColumnName`: (The Name Column to show, e.g., 'Gov')");
            sb.AppendLine("  * `InsertType`: (e.g., 'dropdown' or 'text')");
            sb.AppendLine("  * `DroModel`: (The Lookup Table, e.g., 'GovView')");
            sb.AppendLine("  * `DroValue`: (The PK of Lookup Table, e.g., 'GOVID')");
            sb.AppendLine("  * `DroText`: (The Name Column to show IF `InsertType` == 'dropdown', e.g., 'GovName')");
            sb.AppendLine("\n**STEP 3: TARGET & JOIN LOGIC**");
            sb.AppendLine("You must follow this logic tree precisely to Determine Target Column & Joins:");
            sb.AppendLine("1. **CHECK**: Is `InsertType` == 'dropdown'?");
            sb.AppendLine("   - **NO (Not dropdown)**:");
            sb.AppendLine("       -> Select `ColumnName` directly from current view using its `ColumnName`. **NO JOIN**.");
            sb.AppendLine("   - **YES (Is Dropdown)**: Check `SearchName` vs `ColumnName`:");
            sb.AppendLine("       -> **CASE A (`SearchName` != `ColumnName`)**:");
            sb.AppendLine("           -> Select `ColumnName` directly from current view. **NO JOIN**. (Ignore DroText/DroModel).");
            sb.AppendLine("       -> **CASE B (`SearchName` == `ColumnName`)**:");
            sb.AppendLine("           -> Target is `DroText` (e.g., 'GovName').");
            sb.AppendLine("           -> **MANDATORY OPTIMIZATION CHECK**: Look at the columns in the Current View.");
            sb.AppendLine("               * **IF** a column with the EXACT name `DroText` exists in the Current View:");
            sb.AppendLine("                   -> **STOP**. Select `DroText` from the Current View. **ABSOLUTELY NO JOIN**.");
            sb.AppendLine("               * **ELSE** (Column does NOT exist):");
            sb.AppendLine("                   -> **JOIN** `DroModel` on `SearchName` = `DroValue`. Select `DroText` from `DroModel`.");
            sb.AppendLine("\n   **IMPORTANT NOTE ON ALIASES**:");
            sb.AppendLine("   - Always alias the final selected column using the `arColumnLabel` from ListSetups (e.g., `SELECT GovName AS [الحي]`).");
            sb.AppendLine("\n**STEP 5: VISIBILITY CHECK (For Broad Queries)**");
            sb.AppendLine("- IF the user asks for 'all columns', 'all data', or 'show me the table':");
            sb.AppendLine("  * **NEVER** use `SELECT *`.");
            sb.AppendLine("  * **FILTER** the `ListSetups` entries for the current ListName.");
            sb.AppendLine("  * **SELECT ONLY** columns where the `Visible` property is `true` (or `1`).");
            sb.AppendLine("  * **RESOLVE** each selected column using the **STEP 3 LOGIC** above (Do not blindly join).");
            sb.AppendLine("  * **EXCLUDE** any columns where `Visible` is `false` (or `0`).");
            sb.AppendLine("  * **APPLY** the Aliasing Rule to each selected column (e.g., `SELECT Col1 AS [Label1], Col2 AS [Label2] ...`).");
            sb.AppendLine("\n**STEP 6: PROVENANCE COMMENTS (MANDATORY)**");
            sb.AppendLine("- You MUST append a SQL comment to every selected column AND every JOIN explaining its origin.");
            sb.AppendLine("- FORMAT: `... -- SRC: [Src] | LIST: [ListName] | DRO: [DroModel/NA] | RULE: [Rule] | IDX: [Idx] | MSG: [Msg]`");
            sb.AppendLine("- EXAMPLE: `SELECT [DroText] AS [Gov] -- SRC: GovView.GovName | LIST: PurchaseOrderView | DRO: GovView | RULE: Step 3 Case B | IDX: [12] | MSG: Optimization`");
            sb.AppendLine("- EXAMPLE: `LEFT JOIN [GovView] ON ... -- LIST: PurchaseOrderView | DRO: GovView | RULE: Step 3 Case B | IDX: [12]`");
            sb.AppendLine("- EXAMPLE: `SELECT [Gov] AS [الحي] -- SRC: CurrentView.Gov | LIST: PurchaseOrderView | DRO: N/A | RULE: Step 3 Case A | IDX: [12] | MSG: Direct Select`");

            var result = sb.ToString();
            _staticRulesTokenCount = result.Length / 4; // ~4 chars per token

            return result;
        }
    }
}