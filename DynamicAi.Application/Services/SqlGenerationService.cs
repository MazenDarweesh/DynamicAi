using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

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
        private readonly ILogger<SqlGenerationService> _logger;
        private readonly IGeminiCostTracker _costTracker;

        // ============================================================================
        // GEMINI IMPLICIT CACHE OPTIMIZATION
        // Static rules are cached as a constant string to ensure IDENTICAL prefix
        // for every request, maximizing Gemini's implicit cache hit rate (75% discount)
        // ============================================================================
        private static readonly Lazy<string> _cachedStaticRules = new Lazy<string>(() => BuildStaticSystemRules());
        private static int _staticRulesTokenCount = 0;

        public SqlGenerationService(
            IChatCompletionService chatService,
            ILogger<SqlGenerationService> logger,
            IGeminiCostTracker costTracker)
        {
            _chatService = chatService;
            _logger = logger;
            _costTracker = costTracker;
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
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║              [Layer 2: SQL GENERATION] - STARTING                      ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            _logger.LogInformation("[SqlGeneration] User Question: \"{UserQuestion}\"", userQuestion);
            _logger.LogDebug("[SqlGeneration] Context Bundle Received: UnifiedContext={UnifiedContext}, DictEntries={DictCount}, BusinessRules={RulesCount}",
                !string.IsNullOrEmpty(contextBundle.UnifiedPageContext),
                contextBundle.DictionaryEntries?.Count ?? 0,
                contextBundle.BusinessRules?.Count ?? 0);

            // ============================================================================ 
            // STEP 1: تحليل محتويات Context Bundle
            // ============================================================================ 
            _logger.LogInformation("[SqlGeneration] STEP 1: Analyzing Context Bundle...");

            if (!string.IsNullOrEmpty(contextBundle.UnifiedPageContext))
            {
                _logger.LogInformation("[SqlGeneration] ✓ Unified Page Context available.");
            }
            else
            {
                _logger.LogWarning("[SqlGeneration] ⚠ No Unified Page Context provided");
            }

            if (contextBundle.DictionaryEntries != null && contextBundle.DictionaryEntries.Any())
            {
                _logger.LogInformation("[SqlGeneration] ✓ Dictionary Entries available: {Count}", contextBundle.DictionaryEntries.Count);
            }
            else
            {
                _logger.LogWarning("[SqlGeneration] ⚠ No Dictionary Entries provided");
            }

            if (contextBundle.BusinessRules != null && contextBundle.BusinessRules.Any())
            {
                _logger.LogInformation("[SqlGeneration] ✓ Business Rules available: {Count}", contextBundle.BusinessRules.Count);
            }
            else
            {
                _logger.LogWarning("[SqlGeneration] ⚠ No Business Rules provided");
            }

            // ============================================================================ 
            // STEP 2: بناء الـ Prompt الاحترافي
            // ============================================================================ 
            _logger.LogInformation("[SqlGeneration] STEP 2: Building AI prompt with rich context...");

            var promptStart = DateTime.UtcNow;
            var promptBuilder = new StringBuilder();

            // ============================================================================
            // GEMINI IMPLICIT CACHE OPTIMIZATION
            // Use pre-built static rules (cached once, reused for all requests)
            // This ensures IDENTICAL prefix for every request, maximizing cache hit rate
            // ============================================================================
            promptBuilder.Append(CachedStaticRules);

            _logger.LogInformation("[SqlGeneration] ✓ Static rules loaded from cache ({TokenCount} tokens, ~{CharCount} chars)", StaticRulesTokenCount, CachedStaticRules.Length);
            _logger.LogDebug("[SqlGeneration] ✓ Gemini Implicit Cache: Same prefix for all requests guarantees cache HIT");

            _logger.LogDebug("[SqlGeneration] Building prompt section: HIERARCHY & SCHEMA CONTEXT...");
            promptBuilder.AppendLine("\n--- HIERARCHY & SCHEMA CONTEXT (The available Page structure and its Views) ---");
            if (!string.IsNullOrEmpty(contextBundle.UnifiedPageContext))
            {
                promptBuilder.AppendLine("This JSON object represents the complete page structure. The `SchemaScript` property contains the T-SQL CREATE VIEW statement.");
                promptBuilder.AppendLine(contextBundle.UnifiedPageContext);
                _logger.LogInformation("[SqlGeneration] ✓ Added Unified Page Context.");
            }
            else
            {
                promptBuilder.AppendLine("(No schema context available)");
            }

            _logger.LogDebug("[SqlGeneration] Building prompt section: DICTIONARY CONTEXT...");
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
                _logger.LogInformation("[SqlGeneration] ✓ Added {Count} dictionary entry/entries (COMPACT FORMAT)", contextBundle.DictionaryEntries.Count);
            }
            else
            {
                promptBuilder.AppendLine("(No dictionary context available)");
            }

            _logger.LogDebug("[SqlGeneration] Building prompt section: BUSINESS RULES...");
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
                _logger.LogInformation("[SqlGeneration] ✓ Added {Count} business rule(s)", contextBundle.BusinessRules.Count);
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

            _logger.LogInformation("\n[TokenMetrics] ═══════════════ PROMPT BREAKDOWN ═══════════════");
            _logger.LogInformation("[TokenMetrics]   Static Rules (System):      {Size,8:N0} chars (~{Tokens,6:N0} tokens)", staticRulesSize, staticRulesSize / 4);
            _logger.LogInformation("[TokenMetrics]   Unified Context (Schema):   {Size,8:N0} chars (~{Tokens,6:N0} tokens)", unifiedContextSize, unifiedContextSize / 4);
            _logger.LogInformation("[TokenMetrics]   Dictionary (Compact):       {Size,8:N0} chars (~{Tokens,6:N0} tokens)", dictionarySize, dictionarySize / 4);
            _logger.LogInformation("[TokenMetrics]   Business Rules:             {Size,8:N0} chars (~{Tokens,6:N0} tokens)", businessRulesSize, businessRulesSize / 4);
            _logger.LogInformation("[TokenMetrics]   User Question:              {Size,8:N0} chars (~{Tokens,6:N0} tokens)", userQuestionSize, userQuestionSize / 4);
            _logger.LogInformation("[TokenMetrics]   ─────────────────────────────────────────────────────");
            _logger.LogInformation("[TokenMetrics]   TOTAL PROMPT:               {Size,8:N0} chars (~{Tokens,6:N0} tokens)", totalChars, estimatedTokens);
            _logger.LogInformation("[TokenMetrics]   Est. Input Cost:            ${Cost:F6}", estimatedInputCost);
            _logger.LogInformation("[TokenMetrics] ════════════════════════════════════════════════════════");

            _logger.LogInformation("[SqlGeneration] ✓ Prompt built successfully in {Duration:F0}ms", promptDuration);
            _logger.LogDebug("[SqlGeneration] Prompt size: {Size} characters (~{Tokens} tokens)", fullPrompt.Length, estimatedTokens);

            // ============================================================================ 
            // STEP 3: استدعاء AI Model (Gemini)
            // ============================================================================ 
            _logger.LogInformation("[SqlGeneration] STEP 3: Sending request to Gemini AI...");

            var chatHistory = new ChatHistory();
            chatHistory.AddUserMessage(fullPrompt);

            _logger.LogDebug("[SqlGeneration] Chat history created with 1 user message. Awaiting AI response...");

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

            _logger.LogInformation("[SqlGeneration] ✓ AI response received in {Duration:F0}ms", aiDuration);

            // ============================================================================
            // GEMINI IMPLICIT CACHE DETECTION - Check for cached tokens in response
            // ============================================================================
            int cachedTokens = 0;
            int promptTokens = estimatedTokens; // From our earlier estimation
            int completionTokens = (response.Content?.Length ?? 0) / 4;

            // Try to extract cache info from response metadata
            if (response.Metadata != null)
            {
                _logger.LogDebug("\n[GeminiCache] ═══ RESPONSE METADATA ═══");
                foreach (var kvp in response.Metadata)
                {
                    _logger.LogDebug("[GeminiCache]   {Key}: {Value}", kvp.Key, kvp.Value);

                    // Look for cached token count in various possible keys
                    if (kvp.Key.ToLower().Contains("cached") || kvp.Key.ToLower().Contains("cache"))
                    {
                        if (int.TryParse(kvp.Value?.ToString(), out int cached))
                        {
                            cachedTokens = cached;
                        }
                    }
                }
            }

            // Calculate costs with cache consideration using centralized tracker
            var (totalCost, savings) = _costTracker.CalculateCost(estimatedTokens, completionTokens, cachedTokens);

            // Log centralized tracking event
            _costTracker.TrackUsage("SqlGen", estimatedTokens, completionTokens, cachedTokens);

            // Display cache status and costs
            _logger.LogInformation("\n[GeminiCache] ═══════════════ CACHE & COST ANALYSIS ═══════════════");
            if (cachedTokens > 0)
            {
                _logger.LogInformation("[GeminiCache] ✓ CACHE HIT! {CachedTokens:N0} tokens served from cache", cachedTokens);
                _logger.LogInformation("[GeminiCache]   Savings:           ${Savings:F6}", savings);
            }
            else
            {
                _logger.LogWarning("[GeminiCache] ○ No cache hit detected (first request or prefix changed)");
            }
            _logger.LogInformation("[GeminiCache]   TOTAL COST:        ${TotalCost:F6}", totalCost);
            _logger.LogInformation("[GeminiCache] ════════════════════════════════════════════════════════");

            // ============================================================================ 
            // STEP 4: استخراج وتنظيف SQL
            // ============================================================================ 
            _logger.LogInformation("[SqlGeneration] STEP 4: Extracting and cleaning SQL...");

            var extractStart = DateTime.UtcNow;
            var extractedSql = ExtractSql(response.Content);
            var extractDuration = (DateTime.UtcNow - extractStart).TotalMilliseconds;

            _logger.LogInformation("[SqlGeneration] ✓ SQL extracted and cleaned in {Duration:F0}ms", extractDuration);
            _logger.LogInformation("[SqlGeneration] Final SQL length: {Length} characters", extractedSql.Length);
            _logger.LogDebug("[SqlGeneration] Final SQL: {Sql}", extractedSql);

            // ============================================================================ 
            // [LOGGING] نهاية عملية SQL Generation
            // ============================================================================ 
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║            [Layer 2: SQL GENERATION] - COMPLETED ✓                     ║");
            _logger.LogInformation("║            Total Time: {TotalTime:F0}ms                                               ║", (DateTime.UtcNow - (aiStart - TimeSpan.FromMilliseconds(aiDuration + promptDuration))).TotalMilliseconds);
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            return extractedSql;
        }

        private string ExtractSql(string? content)
        {
            _logger.LogDebug("[SqlGeneration] Cleaning: Starting SQL extraction... Input length: {InputLength}", content?.Length ?? 0);

            if (string.IsNullOrWhiteSpace(content)) return string.Empty;

            content = content.Trim();

            // Regex to match markdown code blocks with optional language identifier
            // Matches ``` followed by optional text (like 'sql', 'tsql'), then whitespace/newline
            var match = System.Text.RegularExpressions.Regex.Match(content, @"^```\w*\s+");
            if (match.Success)
            {
                _logger.LogDebug("[SqlGeneration] Cleaning: Removing markdown start fence '{Fence}'", match.Value.Trim());
                content = content.Substring(match.Length);
            }
            else if (content.StartsWith("```"))
            {
                _logger.LogDebug("[SqlGeneration] Cleaning: Removing '```' prefix");
                content = content.Substring(3);
            }

            if (content.EndsWith("```"))
            {
                _logger.LogDebug("[SqlGeneration] Cleaning: Removing '```' suffix");
                content = content.Substring(0, content.Length - 3);
            }

            var cleaned = content.Trim();

            _logger.LogDebug("[SqlGeneration] Cleaning: Output length: {OutputLength}", cleaned.Length);

            return cleaned;
        }

        /// <summary>
        /// Builds all static system rules once and caches them for reuse.
        /// This maximizes Gemini's implicit caching by ensuring identical prefix every request.
        /// </summary>
        private static string BuildStaticSystemRules()
        {
            // Note: Since this is static and used in Lazy initialization, we can't easily use the instance logger here
            // unless we change how it's initialized. For now, we'll keep it silent or use a static logger if needed.
            // Leaving Console.WriteLine here might interrupt the flow, but it only runs once per app lifetime.
            // Converting to basic trace or removing.
            System.Diagnostics.Debug.WriteLine("🔄 [Cache] Building Static Rules - Version 4.0");
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
            sb.AppendLine("- ⚠️ **CRITICAL COMMA RULE**: The comma (,) MUST come BEFORE the comment `--`, NEVER inside or after the comment!");
            sb.AppendLine("  * ❌ WRONG: `T1.Col AS [Label] -- comment,` (comma inside comment = SYNTAX ERROR!)");
            sb.AppendLine("  * ✅ CORRECT: `T1.Col AS [Label],  -- comment` (comma before comment = VALID SQL)");
            sb.AppendLine("- EXAMPLE: `SELECT [DroText] AS [Gov],  -- SRC: GovView.GovName | LIST: PurchaseOrderView | DRO: GovView | RULE: Step 3 Case B | IDX: [12] | MSG: Optimization`");
            sb.AppendLine("- EXAMPLE: `LEFT JOIN [GovView] ON ... -- LIST: PurchaseOrderView | DRO: GovView | RULE: Step 3 Case B | IDX: [12]`");
            sb.AppendLine("- EXAMPLE: `SELECT [Gov] AS [الحي],  -- SRC: CurrentView.Gov | LIST: PurchaseOrderView | DRO: N/A | RULE: Step 3 Case A | IDX: [12] | MSG: Direct Select`");

            var result = sb.ToString();
            _staticRulesTokenCount = result.Length / 4; // ~4 chars per token

            return result;
        }
    }
}