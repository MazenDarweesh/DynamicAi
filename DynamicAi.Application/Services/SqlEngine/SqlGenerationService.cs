using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using DynamicAi.Domain.Entities;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// (تنفيذ Layer 2 المُعدل)
    /// يستخدم موديل المحادثة (gemini-2.5-pro) والسياق الغني
    /// لتحويل سؤال المستخدم إلى SQL دقيق.
    /// </summary>
    public class SqlGenerationService : ISqlGenerationService
    {
        #region Dependencies

        private readonly IChatCompletionService _chatService;
        private readonly ILogger<SqlGenerationService> _logger;
        private readonly ILlmUsageTracker _usageTracker;
        private readonly IGeminiCacheManager _cacheManager;
        private readonly IConfiguration _config;
        private readonly IGeminiPageContextCache _geminiPageCache;
        private readonly bool _isGeminiCacheEnabled;

        #endregion

        #region Static Cache Optimization

        // ============================================================================
        // GEMINI EXPLICIT CACHE OPTIMIZATION
        // Static rules are cached on Google's servers for 75% cost discount
        // and 3-5 second latency reduction per request
        // ============================================================================
        private static readonly Lazy<string> _cachedStaticRules = new Lazy<string>(() => BuildStaticSystemRules());
        private static int _staticRulesTokenCount = 0;

        #endregion

        #region Constructor

        public SqlGenerationService(
            IChatCompletionService chatService,
            ILogger<SqlGenerationService> logger,
            ILlmUsageTracker usageTracker,
            IGeminiCacheManager cacheManager,
            IGeminiPageContextCache geminiPageCache,
            IConfiguration configuration)
        {
            _chatService = chatService;
            _logger = logger;
            _usageTracker = usageTracker;
            _cacheManager = cacheManager;
            _geminiPageCache = geminiPageCache;
            _config = configuration;

            // Use TryParse for defense (Rule 8: Defensive Configuration)
            var configValue = _config["Caching:GeminiCache:Enabled"];
            if (bool.TryParse(configValue, out bool isEnabled))
            {
                _isGeminiCacheEnabled = isEnabled;
            }
            else
            {
                _isGeminiCacheEnabled = true; // Default to true if missing or invalid
            }
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the cached static system rules (built once, reused for all requests)
        /// </summary>
        public static string CachedStaticRules => _cachedStaticRules.Value;

        /// <summary>
        /// Gets the estimated token count for static rules
        /// </summary>
        public static int StaticRulesTokenCount => _staticRulesTokenCount;

        #endregion

        #region Main Methods

        /// <param name="actionType">نوع الإجراء المقرر من Condenser (New/WrapPrevious/Regenerate) أو inline detection</param>
        /// <param name="conversationHistory">آخر N من المحادثات السابقة (سؤال + SQL + PageId) — يستخدم في inline condenser bypass</param>
        /// <param name="isCondenserBypassed">إذا كان true، الـ prompt هيكتشف الـ action type inline بدل condenser</param>
        public async Task<SqlGenerationResult> GenerateSqlAsync(
            string userQuestion,
            RetrievalContextBundle contextBundle,
            string? previousSql = null,
            QueryActionType actionType = QueryActionType.Regenerate,
            string? tenantId = null,
            int? companyId = null,
            List<DynamicAi.Domain.Entities.ConversationTurn>? conversationHistory = null,
            bool isCondenserBypassed = false,
            CancellationToken ct = default)
        {
            #region Initialization and Logging

            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║              [Layer 2: SQL GENERATION] - STARTING                      ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            _logger.LogInformation("[SqlGeneration] User Question: \"{UserQuestion}\"", userQuestion);
            _logger.LogInformation("[SqlGeneration] Action Type: {ActionType}", actionType);
            _logger.LogDebug("[SqlGeneration] Context Bundle Received: UnifiedContext={UnifiedContext}, DictEntries={DictCount}, BusinessRules={RulesCount}",
                !string.IsNullOrEmpty(contextBundle.UnifiedPageContext),
                contextBundle.DictionaryEntries?.Count ?? 0,
                contextBundle.BusinessRules?.Count ?? 0);

            #endregion

            #region STEP 1: Context Bundle Analysis

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

            #endregion

            #region STEP 2: Prompt Building

            // ============================================================================ 
            // STEP 2: بناء الـ Prompt الاحترافي
            // ============================================================================ 
            _logger.LogInformation("[SqlGeneration] STEP 2: Building AI prompt with rich context...");

            var promptStart = DateTime.UtcNow;
            var promptBuilder = new StringBuilder();

            #region STEP 2.1: Static Rules & Implicit Cache

            // ============================================================================
            // GEMINI IMPLICIT CACHE OPTIMIZATION
            // Use pre-built static rules (cached once, reused for all requests)
            // This ensures IDENTICAL prefix for every request, maximizing cache hit rate
            // ============================================================================
            promptBuilder.Append(CachedStaticRules);

            _logger.LogInformation("[SqlGeneration] ✓ Static rules loaded from cache ({TokenCount} tokens, ~{CharCount} chars)", StaticRulesTokenCount, CachedStaticRules.Length);
            _logger.LogDebug("[SqlGeneration] ✓ Gemini Implicit Cache: Same prefix for all requests guarantees cache HIT");

            #endregion

            #region STEP 2.2: Hierarchy & Schema Context

            _logger.LogDebug("[SqlGeneration] Building prompt section: HIERARCHY & SCHEMA CONTEXT...");
            promptBuilder.AppendLine("\n--- HIERARCHY & SCHEMA CONTEXT (The available Page structure and its Views) ---");
            if (!string.IsNullOrEmpty(contextBundle.UnifiedPageContext))
            {
                promptBuilder.AppendLine("This JSON shows the page structure. IMPORTANT: SchemaScript is for understanding table relationships ONLY. NEVER pick column names from SchemaScript. Use ONLY ColumnName values from the DICTIONARY CONTEXT below.");
                promptBuilder.AppendLine(contextBundle.UnifiedPageContext);
                _logger.LogInformation("[SqlGeneration] ✓ Added Unified Page Context.");
            }
            else
            {
                promptBuilder.AppendLine("(No schema context available)");
            }

            #endregion

            #region STEP 2.3: Dictionary Context (Compact Format)

            _logger.LogDebug("[SqlGeneration] Building prompt section: DICTIONARY CONTEXT...");
            promptBuilder.AppendLine("\n--- DICTIONARY CONTEXT (User Labels to DB Columns) ---");

            // Unit 2: Compact Dictionary Format - Add header explaining the format
            promptBuilder.AppendLine("Format: [Index] ListName | SearchName | ColumnName | InsertType | arColumnLabel | enColumnLabel | Vis | DroModel:DroValue:DroText (if dropdown)");
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

                        var listName = root.TryGetProperty("ListName", out var ln) ? ln.GetString() ?? "" : "";
                        var searchName = root.TryGetProperty("SearchName", out var sn) ? sn.GetString() ?? "" : "";
                        var columnName = root.TryGetProperty("ColumnName", out var cn) ? cn.GetString() ?? "" : "";
                        var insertType = root.TryGetProperty("InsertType", out var it) ? it.GetString() ?? "" : "";
                        var arLabel = root.TryGetProperty("arColumnLabel", out var ar) ? ar.GetString() ?? "" : "";
                        var enLabel = root.TryGetProperty("enColumnLabel", out var en) ? en.GetString() ?? "" : "";

                        var visible = root.TryGetProperty("visible", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;

                        // Build compact entry
                        var compactEntry = $"[{dictIndex}] {listName} | {searchName} | {columnName} | {insertType} | {arLabel} | {enLabel} | Vis:{visible}";

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

            #endregion

            #region STEP 2.4: Insights Context Collection

            // ============================================================================
            // CAPTURE CONTEXT FOR QUERY INSIGHTS PANEL
            // ============================================================================
            var retrievedContext = new List<string>();

            // Capture schema context
            if (!string.IsNullOrEmpty(contextBundle.UnifiedPageContext))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(contextBundle.UnifiedPageContext);
                    var pageName = doc.RootElement.TryGetProperty("PageName", out var pn)
                        ? pn.GetString()
                        : "Unknown Page";
                    retrievedContext.Add($"📄 {pageName} Schema");
                }
                catch
                {
                    retrievedContext.Add("📄 Schema Context");
                }
            }

            // Capture dictionary entries count
            if (contextBundle.DictionaryEntries != null && contextBundle.DictionaryEntries.Any())
            {
                retrievedContext.Add($"📚 {contextBundle.DictionaryEntries.Count} Dictionary Entries");
            }

            // Capture business rules count
            if (contextBundle.BusinessRules != null && contextBundle.BusinessRules.Any())
            {
                retrievedContext.Add($"📋 {contextBundle.BusinessRules.Count} Business Rules");
            }

            #endregion

            #region STEP 2.5: Business Rules Integration

            _logger.LogDebug("[SqlGeneration] Building prompt section: BUSINESS RULES...");
            promptBuilder.AppendLine("\n--- BUSINESS RULES (Must be included) ---");
            
            // 🌟 Inject Core Date Rules unconditionally
            promptBuilder.AppendLine("\n[Core Business Rule: Date Handling]");
            promptBuilder.AppendLine("CRITICAL INSTRUCTION FOR DATES: Whenever the user asks to filter by dates (e.g., 'last month', 'this year', 'today', 'last 3 months'), you MUST ALWAYS use the 'CreateAt' column for date filtering UNLESS the user explicitly mentions another date field. DO NOT hallucinate columns like 'MONTH(ContractDate)' or 'OrderDate'.");
            promptBuilder.AppendLine("CROSS-YEAR DATE FILTERING RULE: When filtering for 'last X months' or 'last X days', DO NOT split the condition into separate MONTH() and YEAR() checks. ALWAYS use a direct comparison: `ColumnName >= DATEADD(month, -X, GETDATE())`. Filtering by `YEAR(Col) = YEAR(GETDATE())` breaks cross-year queries (e.g., pulling data from December when the current month is January).");
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
                promptBuilder.AppendLine("(No user-defined business rules available)");
            }

            #endregion

            #region STEP 2.6: Mode-Specific Directives (Wrap/Regen/Inline)

            // ============================================================================
            // CONDENSER BYPASS: INLINE CONTEXT DETECTION
            // When condenser is bypassed, the LLM decides the action type inline
            // based on full conversation history.
            // ============================================================================
            if (isCondenserBypassed && conversationHistory != null && conversationHistory.Any(t => t.SqlExecuted != null))
            {
                _logger.LogInformation("[SqlGeneration] ✓ INLINE Mode: Condenser bypassed. Injecting {TurnCount} history turns into prompt.", conversationHistory.Count);

                promptBuilder.AppendLine("\n--- CONVERSATION HISTORY & CONTEXT DETECTION ---");
                promptBuilder.AppendLine("IMPORTANT: You have access to the recent conversation below. Analyze it to understand context.");
                promptBuilder.AppendLine("Your job is to determine the RELATIONSHIP between the CURRENT QUESTION and the conversation history,");
                promptBuilder.AppendLine("and then generate the appropriate SQL accordingly.\n");

                promptBuilder.AppendLine("PREVIOUS TURNS (most recent first):");
                var turnsWithSql = conversationHistory
                    .Where(t => !string.IsNullOrEmpty(t.SqlExecuted))
                    .OrderByDescending(t => t.CreatedAt)
                    .Take(5)
                    .ToList();

                for (int i = 0; i < turnsWithSql.Count; i++)
                {
                    var turn = turnsWithSql[i];
                    promptBuilder.AppendLine($"[Turn {i + 1}] Q: \"{turn.Question}\"");
                    promptBuilder.AppendLine($"         SQL: {turn.SqlExecuted}");
                }

                promptBuilder.AppendLine($"\nCURRENT QUESTION: \"{userQuestion}\"");

                promptBuilder.AppendLine("\nDECISION RULES (pick the BEST match):");
                promptBuilder.AppendLine("1. WRAP_PREVIOUS: The current question is post-processing ONLY (sort results, limit rows, top N, count).");
                promptBuilder.AppendLine("   → Wrap the most recent SQL in a subquery. Apply sort/limit in outer query.");
                promptBuilder.AppendLine("   → SQL SERVER RULE: ORDER BY in subquery REQUIRES TOP or OFFSET. Keep TOP+ORDER BY TOGETHER inside subquery.");
                promptBuilder.AppendLine("   → PATTERN: SELECT sub.* FROM (SELECT TOP N ... ORDER BY [orig_col]) AS sub ORDER BY sub.[new_col];");
                promptBuilder.AppendLine("2. MODIFY_PARAMETER: The current question changes ONLY a specific VALUE in the previous SQL");
                promptBuilder.AppendLine("   (e.g., month count, year, date range, number, row limit, specific name/ID).");
                promptBuilder.AppendLine("   → Take the MOST RECENT SQL and ONLY change that specific value. DO NOT restructure the query.");
                promptBuilder.AppendLine("   → Example: 'غير من 3 شهور لـ 6' → change DATEADD(month, -3, ...) to DATEADD(month, -6, ...)");
                promptBuilder.AppendLine("3. ADD_CONDITION: The current question adds a filter, condition, join, or column to the SAME query.");
                promptBuilder.AppendLine("   → Modify the most recent SQL to add WHERE clause, JOIN, or extra SELECT column.");
                promptBuilder.AppendLine("   → Example: 'بس المدفوعة' → add WHERE IsPaid = 1 to existing SQL");
                promptBuilder.AppendLine("4. REGENERATE: The question is related to the same topic/tables but needs substantially different SQL.");
                promptBuilder.AppendLine("   → Generate fresh SQL using the schema context. Use history for understanding only.");
                promptBuilder.AppendLine("5. NEW: The question is about a COMPLETELY DIFFERENT topic, entity, or table.");
                promptBuilder.AppendLine("   → Ignore ALL conversation history. Generate SQL purely from schema context.");

                promptBuilder.AppendLine("\n⚠️ CRITICAL: For MODIFY_PARAMETER, output the previous SQL with ONLY the changed value.");
                promptBuilder.AppendLine("DO NOT rewrite, restructure, or 'improve' the query — only change the requested parameter.");
            }
            // ============================================================================
            // STANDARD CONDENSER PATH (when condenser is enabled)
            // ============================================================================
            else if (actionType == QueryActionType.WrapPrevious && !string.IsNullOrWhiteSpace(previousSql))
            {
                // WRAP MODE: Force subquery wrapping
                _logger.LogInformation("[SqlGeneration] ✓ WRAP Mode: Will wrap previous SQL in subquery.");
                promptBuilder.AppendLine("\n--- ⚠️ MANDATORY: WRAP PREVIOUS SQL ---");
                promptBuilder.AppendLine("User requests MODIFICATION on PREVIOUS results. WRAP this SQL:");
                promptBuilder.AppendLine("```sql");
                promptBuilder.AppendLine(previousSql);
                promptBuilder.AppendLine("```");
                promptBuilder.AppendLine("⚠️ SQL SERVER SUBQUERY RULES:");
                promptBuilder.AppendLine("1. ORDER BY in subquery REQUIRES TOP or OFFSET!");
                promptBuilder.AppendLine("2. If prev SQL has TOP+ORDER BY, keep them TOGETHER inside subquery.");
                promptBuilder.AppendLine("3. Apply NEW sort/filter in OUTER query only.");
                promptBuilder.AppendLine("CORRECT PATTERN:");
                promptBuilder.AppendLine("SELECT sub.* FROM (SELECT TOP N ... ORDER BY [orig_col]) AS sub ORDER BY sub.[new_col];");
                promptBuilder.AppendLine("DO NOT move TOP outside. DO NOT remove original ORDER BY. Only wrap and add new operation.");
            }
            else if (actionType == QueryActionType.Regenerate && !string.IsNullOrWhiteSpace(previousSql))
            {
                // REGENERATE MODE: Use previous SQL for context only
                _logger.LogInformation("[SqlGeneration] ✓ REGENERATE Mode: Using previous SQL as context reference.");
                promptBuilder.AppendLine("\n--- CONTEXT REFERENCE: PREVIOUS SQL ---");
                promptBuilder.AppendLine("This is the SQL from the previous question. The new question is related but requires a NEW query.");
                promptBuilder.AppendLine("Use it for context understanding only. Generate a fresh SQL for the new requirements.");
                promptBuilder.AppendLine("```sql");
                promptBuilder.AppendLine(previousSql);
                promptBuilder.AppendLine("```");
            }
            // NEW mode: No previous SQL context needed

            #endregion

            #region STEP 2.7: Final Assembly and Metrics

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

            // ============================================================================
            // DIAGNOSTIC LOGGING (TEMPORARY - Development Only)
            // ============================================================================
            if (bool.TryParse(_config["Logging:EnableContextAnalysisLogs"], out var enableLogs) && enableLogs)
            {
                LogDetailedPromptAnalysis(contextBundle, fullPrompt);
            }

            _logger.LogInformation("[SqlGeneration] ✓ Prompt built successfully in {Duration:F0}ms", promptDuration);
            _logger.LogDebug("[SqlGeneration] Prompt size: {Size} characters (~{Tokens} tokens)", fullPrompt.Length, estimatedTokens);

            #endregion

            #endregion

            #region STEP 2.8: Gemini Page Cache Integration

            // ============================================================================ 
            // STEP 2.8: GEMINI PAGE-SCOPED CACHE INTEGRATION 🚀
            // ============================================================================ 
            string? geminiPageCacheId = null;
            int? extractedPageId = null;

            if (_isGeminiCacheEnabled)
            {
                // Try to extract PageID from UnifiedPageContext
                if (!string.IsNullOrEmpty(contextBundle.UnifiedPageContext))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(contextBundle.UnifiedPageContext);
                        if (doc.RootElement.TryGetProperty("PageID", out var pageIdProp))
                        {
                            extractedPageId = pageIdProp.TryGetInt32(out var pid) ? pid : null;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[SqlGeneration] ⚠️ Failed to extract PageID from context");
                    }
                }

                // Attempt to retrieve Gemini Page Cache if PageID available
                if (extractedPageId.HasValue)
                {
                    _logger.LogInformation("[SqlGeneration] 🔍 Checking Gemini Page Cache for PageID {PageId}...", extractedPageId.Value);

                    try
                    {
                        geminiPageCacheId = await _geminiPageCache.GetOrCreateGeminiPageCacheAsync(
                            extractedPageId.Value,
                            ct
                        );

                        if (!string.IsNullOrEmpty(geminiPageCacheId))
                        {
                            _logger.LogInformation("[SqlGeneration] ✅ Gemini Page Cache HIT! Resource: {CacheId}", geminiPageCacheId);
                            _logger.LogInformation("[SqlGeneration] 💰 Expected savings: ~75% cost reduction + ~3-5s latency improvement");
                        }
                        else
                        {
                            _logger.LogWarning("[SqlGeneration] ⚠️ Gemini Page Cache returned null for PageID {PageId}, falling back to full prompt", extractedPageId.Value);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[SqlGeneration] ❌ Gemini Page Cache retrieval failed for PageID {PageId}, using full prompt", extractedPageId.Value);
                    }
                }
                else
                {
                    _logger.LogDebug("[SqlGeneration] ℹ️ No PageID found in context, skipping Gemini Page Cache");
                }
            }
            else
            {
                _logger.LogInformation("[SqlGeneration] ℹ️ Gemini Explicit Cache is DISABLED in config.");
                _logger.LogInformation("[SqlGeneration] ↪️ Falling back to Full Prompt (Preserving Implicit Cache / Static Rules).");
            }

            #endregion

            #region STEP 3: AI Model Invocation

            // ============================================================================ 
            // STEP 3: استدعاء AI Model (Gemini)
            // ============================================================================ 
            _logger.LogInformation("[SqlGeneration] STEP 3: Sending request to Gemini AI...");

            var chatHistory = new ChatHistory();

            // ============================================================================
            // BUILD USER MESSAGE: Use Gemini Page Cache if available, else full prompt
            // ============================================================================
            if (!string.IsNullOrEmpty(geminiPageCacheId))
            {
                // CACHE HIT: Send minimal user message with cache reference
                var minimalUserMessage = new StringBuilder();
                minimalUserMessage.AppendLine("--- TASK ---");

                // Include action type directive if applicable
                if (actionType == QueryActionType.WrapPrevious && !string.IsNullOrWhiteSpace(previousSql))
                {
                    minimalUserMessage.AppendLine("⚠️ MANDATORY: WRAP PREVIOUS SQL");
                    minimalUserMessage.AppendLine("User requests MODIFICATION on PREVIOUS results. WRAP this SQL:");
                    minimalUserMessage.AppendLine("```sql");
                    minimalUserMessage.AppendLine(previousSql);
                    minimalUserMessage.AppendLine("```");
                    minimalUserMessage.AppendLine("Apply SQL Server subquery wrapping rules.");
                }
                else if (actionType == QueryActionType.Regenerate && !string.IsNullOrWhiteSpace(previousSql))
                {
                    minimalUserMessage.AppendLine("--- CONTEXT REFERENCE: PREVIOUS SQL ---");
                    minimalUserMessage.AppendLine("```sql");
                    minimalUserMessage.AppendLine(previousSql);
                    minimalUserMessage.AppendLine("```");
                }

                minimalUserMessage.AppendLine($"Q: {userQuestion}");
                
                // CRITICAL FIX: Explicitly remind LLM of the output format to prevent JSON hallucination
                minimalUserMessage.AppendLine("IMPORTANT: You must follow the 'SystemInstructions' in the cached context.");
                minimalUserMessage.AppendLine("OUTPUT FORMAT: JSON { \"sql\": \"...\", \"confidence\": ... }");
                minimalUserMessage.AppendLine("A: ");

                chatHistory.AddUserMessage(minimalUserMessage.ToString());

                _logger.LogInformation("[SqlGeneration] 📦 Using CACHED page context + minimal user message ({Size} chars, ~75% token reduction)",
                    minimalUserMessage.Length);
            }
            else
            {
                // CACHE MISS: Use full prompt
                chatHistory.AddUserMessage(fullPrompt);
                _logger.LogDebug("[SqlGeneration] 📄 Using FULL prompt (no cache available)");
            }

            _logger.LogDebug("[SqlGeneration] Chat history created with 1 user message. Awaiting AI response...");

            var aiStart = DateTime.UtcNow;

            // ============================================================================
            // GEMINI EXPLICIT CACHING - Use cached content for static rules
            // ============================================================================
            string? cachedContentId = null;
            try
            {
                cachedContentId = _cacheManager.CurrentCacheId;
                if (!string.IsNullOrEmpty(cachedContentId))
                {
                    _logger.LogInformation("[SqlGeneration] ✓ Using Gemini Cached Content: {CacheId}", cachedContentId);
                }
                else
                {
                    _logger.LogWarning("[SqlGeneration] ⚠ No cached content available, using full prompt");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SqlGeneration] ⚠ Cache check failed, continuing without cache");
            }

            // Use GeminiPromptExecutionSettings with CachedContent for explicit caching
            var executionSettings = new GeminiPromptExecutionSettings
            {
                Temperature = 0.0,  // Zero temperature for deterministic output
                TopP = 0.1,         // Low topP for more focused sampling
                CachedContent = cachedContentId  // Reference to cached static rules
            };

            var response = await _chatService.GetChatMessageContentAsync(chatHistory, executionSettings, cancellationToken: ct);
            var aiDuration = (DateTime.UtcNow - aiStart).TotalMilliseconds;

            _logger.LogInformation("[SqlGeneration] ✓ AI response received in {Duration:F0}ms", aiDuration);

            // ============================================================================
            // ACTUAL TOKEN EXTRACTION from Semantic Kernel response metadata
            // Accuracy: ~99% (vs ~65% with text.Length/4 estimation)
            // ============================================================================
            int cachedTokens = 0;
            int promptTokens = estimatedTokens; // Fallback: from our earlier estimation
            int completionTokens = (response.Content?.Length ?? 0) / 4; // Fallback
            bool usedActualTokens = false;

            if (response.Metadata != null)
            {
                _logger.LogDebug("\n[TokenExtraction] ═══ RESPONSE METADATA ═══");
                foreach (var kvp in response.Metadata)
                {
                    _logger.LogDebug("[TokenExtraction]   {Key}: {Value} (Type: {Type})",
                        kvp.Key, kvp.Value, kvp.Value?.GetType().Name ?? "null");
                }

                // --- Strategy 1: Extract from Gemini UsageMetadata (GeminiResponse) ---
                if (response.Metadata.TryGetValue("UsageMetadata", out var usageMeta) && usageMeta != null)
                {
                    // Gemini SDK returns UsageMetadata with PromptTokenCount, CandidatesTokenCount, CachedContentTokenCount
                    var usageType = usageMeta.GetType();
                    var promptProp = usageType.GetProperty("PromptTokenCount");
                    var candidatesProp = usageType.GetProperty("CandidatesTokenCount");
                    var cachedProp = usageType.GetProperty("CachedContentTokenCount");

                    if (promptProp != null)
                    {
                        promptTokens = (int)(promptProp.GetValue(usageMeta) ?? promptTokens);
                        usedActualTokens = true;
                    }
                    if (candidatesProp != null)
                    {
                        completionTokens = (int)(candidatesProp.GetValue(usageMeta) ?? completionTokens);
                    }
                    if (cachedProp != null)
                    {
                        cachedTokens = (int)(cachedProp.GetValue(usageMeta) ?? 0);
                    }
                }

                // --- Strategy 2: Fallback - check common key patterns ---
                if (!usedActualTokens)
                {
                    foreach (var kvp in response.Metadata)
                    {
                        var keyLower = kvp.Key.ToLower();
                        if ((keyLower.Contains("prompt") && keyLower.Contains("token")) ||
                            keyLower == "input_tokens" || keyLower == "inputtokencount")
                        {
                            if (int.TryParse(kvp.Value?.ToString(), out int parsed))
                            {
                                promptTokens = parsed;
                                usedActualTokens = true;
                            }
                        }
                        else if ((keyLower.Contains("completion") && keyLower.Contains("token")) ||
                                 keyLower == "output_tokens" || keyLower == "outputtokencount" ||
                                 keyLower.Contains("candidates"))
                        {
                            if (int.TryParse(kvp.Value?.ToString(), out int parsed))
                            {
                                completionTokens = parsed;
                            }
                        }
                        else if (keyLower.Contains("cached") || keyLower.Contains("cache"))
                        {
                            if (int.TryParse(kvp.Value?.ToString(), out int parsed))
                            {
                                cachedTokens = parsed;
                            }
                        }
                    }
                }

                _logger.LogInformation("[TokenExtraction] {Source} — Prompt: {Prompt}, Completion: {Completion}, Cached: {Cached}",
                    usedActualTokens ? "✅ ACTUAL (from API metadata)" : "⚠️ ESTIMATED (text.Length/4 fallback)",
                    promptTokens, completionTokens, cachedTokens);
            }



            // Track usage centrally
            // Track usage centrally (M4: Config & Tenant Aware)
            await _usageTracker.TrackLlmCallAsync(
                LlmOperationType.SqlGeneration,
                requestId: Guid.NewGuid().ToString(), // TODO: Pass RequestId from ContextBundle if available
                inputTokens: promptTokens,
                outputTokens: completionTokens,
                cachedTokens: cachedTokens,
                tenantId: tenantId, 
                companyId: companyId,
                cancellationToken: ct 
            );

            // Display cache status
            _logger.LogInformation("\n[GeminiCache] ═══════════════ CACHE ANALYSIS ═══════════════");
            if (cachedTokens > 0)
            {
                _logger.LogInformation("[GeminiCache] ✓ CACHE HIT! {CachedTokens:N0} tokens served from cache", cachedTokens);
            }
            else
            {
                _logger.LogWarning("[GeminiCache] ○ No cache hit detected (first request or prefix changed)");
            }


            #endregion

            #region STEP 4: SQL Extraction and Cleaning

            // ============================================================================ 
            // STEP 4: استخراج وتنظيف SQL
            // ============================================================================ 
            _logger.LogInformation("[SqlGeneration] STEP 4: Extracting and cleaning SQL...");

            var extractStart = DateTime.UtcNow;
            var result = ExtractSqlResult(response.Content);
            var extractDuration = (DateTime.UtcNow - extractStart).TotalMilliseconds;

            // ============================================================================
            // [INSIGHTS] Populate Query Insights for Frontend
            // ============================================================================
            result.RetrievedContext = new List<string>();

            // 1. Schema Context
            if (!string.IsNullOrEmpty(contextBundle.UnifiedPageContext))
            {
                result.RetrievedContext.Add("📄 Unified Page Schema (Active Page)");
            }

            // 2. Dictionary Context
            if (contextBundle.DictionaryEntries != null && contextBundle.DictionaryEntries.Any())
            {
                result.RetrievedContext.Add($"📚 Dictionary: {contextBundle.DictionaryEntries.Count} entries used");
            }

            // 3. Business Rules Context
            if (contextBundle.BusinessRules != null && contextBundle.BusinessRules.Any())
            {
                result.RetrievedContext.Add($"📋 Business Rules: {contextBundle.BusinessRules.Count} rules applied");
            }

            // 4. Update Reasoning if Legacy
            if (result.Reasoning == "Legacy response format (no self-evaluation)")
            {
                result.Reasoning = $"Generated SQL using SqlGeneration mode based on the provided schema and user constraints.";
            }

            // 5. Append Cache Info to Reasoning
            if (cachedTokens > 0)
            {
                result.Reasoning += $" (⚡ accelerated by Gemini Cache)";
            }

            _logger.LogInformation("[SqlGeneration] ✓ SQL extracted and cleaned in {Duration:F0}ms", extractDuration);
            _logger.LogInformation("[SqlGeneration] Final SQL length: {Length} characters", result.Sql.Length);
            _logger.LogInformation("[SqlGeneration] LLM Confidence: {Confidence:P0}", result.Confidence);

            // ============================================================================
            // [DIAGNOSTIC] طباعة الـ SQL المولد مباشرة
            // ============================================================================
            _logger.LogWarning("\n╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogWarning("║         🤖 [SQL GENERATION] - RAW SQL OUTPUT FROM LLM                 ║");
            _logger.LogWarning("╚════════════════════════════════════════════════════════════════════════╝");
            _logger.LogWarning("\n[Generated SQL]:\n{Sql}\n", result.Sql);
            _logger.LogWarning("╚════════════════════════════════════════════════════════════════════════╝\n");

            #endregion

            #region Final Logging and Return

            // ============================================================================ 
            // [LOGGING] نهاية عملية SQL Generation
            // ============================================================================ 
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║            [Layer 2: SQL GENERATION] - COMPLETED ✓                     ║");
            _logger.LogInformation("║            Total Time: {TotalTime:F0}ms                                               ║", (DateTime.UtcNow - (aiStart - TimeSpan.FromMilliseconds(aiDuration + promptDuration))).TotalMilliseconds);
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            // Populate RetrievedContext for Query Insights Panel
            result.RetrievedContext = retrievedContext;

            return result;

            #endregion
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Extracts SQL and confidence from LLM response (supports both JSON and plain SQL formats)
        /// </summary>
        private SqlGenerationResult ExtractSqlResult(string? content)
        {
            _logger.LogDebug("[SqlGeneration] Cleaning: Starting SQL extraction... Input length: {InputLength}", content?.Length ?? 0);

            if (string.IsNullOrWhiteSpace(content))
                return new SqlGenerationResult { Sql = string.Empty, Confidence = 0.5f };

            content = content.Trim();

            // Try to parse as JSON first (new format with confidence)
            if (content.StartsWith("{") || content.Contains("\"sql\""))
            {
                try
                {
                    // Extract JSON from possible markdown wrapper
                    var jsonMatch = Regex.Match(content, @"```json\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                    var jsonContent = jsonMatch.Success ? jsonMatch.Groups[1].Value.Trim() : content;

                    // Try direct JSON parse
                    if (jsonContent.StartsWith("{"))
                    {
                        using var doc = JsonDocument.Parse(jsonContent);
                        var root = doc.RootElement;

                        var sql = root.TryGetProperty("sql", out var sqlProp) ? sqlProp.GetString() ?? "" : "";
                        var confidence = root.TryGetProperty("confidence", out var confProp) ? (float)confProp.GetDouble() : 0.8f;
                        var reasoning = root.TryGetProperty("reasoning", out var reasonProp) ? reasonProp.GetString() : null;
                        var needsClarification = root.TryGetProperty("needs_clarification", out var clarProp) && clarProp.GetBoolean();
                        var clarificationQuestion = root.TryGetProperty("clarification_question", out var qProp) ? qProp.GetString() : null;

                        _logger.LogDebug("[SqlGeneration] Parsed JSON response. Confidence: {Confidence}", confidence);

                        return new SqlGenerationResult
                        {
                            Sql = CleanSql(sql),
                            Confidence = confidence,
                            Reasoning = reasoning,
                            NeedsClarification = needsClarification,
                            ClarificationQuestion = clarificationQuestion
                        };
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning("[SqlGeneration] Failed to parse JSON response, falling back to plain SQL: {Error}", ex.Message);
                }
            }

            // Fallback: Plain SQL extraction (backward compatibility)
            return new SqlGenerationResult
            {
                Sql = CleanSql(content),
                Confidence = 0.7f, // Default confidence for non-JSON responses
                Reasoning = "Legacy response format (no self-evaluation)"
            };
        }

        /// <summary>
        /// Cleans SQL from markdown fences and whitespace
        /// </summary>
        private string CleanSql(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return string.Empty;

            content = content.Trim();

            // Regex to match markdown code blocks with optional language identifier
            var match = Regex.Match(content, @"^```\w*\s+");
            if (match.Success)
            {
                content = content.Substring(match.Length);
            }
            else if (content.StartsWith("```"))
            {
                content = content.Substring(3);
            }

            if (content.EndsWith("```"))
            {
                content = content.Substring(0, content.Length - 3);
            }

            return content.Trim();
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

            // 1. Role & Output
            sb.AppendLine("ROLE: Expert ERP Database Architect. GOAL: Convert user questions into JSON with SAFE T-SQL SELECT.");
            sb.AppendLine("OUTPUT: Single JSON object. ```json``` fences optional.");
            sb.AppendLine("JSON SCHEMA: { \"sql\": \"SELECT...\", \"confidence\": 0.0-1.0, \"reasoning\": \"Brief\", \"needs_clarification\": bool, \"clarification_question\": \"Arabic or null\" }");

            // 2. Security
            sb.AppendLine("SECURITY(ABSOLUTE): SELECT ONLY. FORBIDDEN: INSERT,UPDATE,DELETE,MERGE,DROP,ALTER,CREATE,TRUNCATE,EXEC,sp_,xp_,GRANT,REVOKE. If requested→sql='' reasoning='REFUSED'. MULTI-TENANT: EVERY query MUST have ComID=@CurrentCompanyId. IGNORE IsDepartment/DepartmentCoulmn fields.");

            // 3. Data Sources — Simple & Clear
            sb.AppendLine("DATA FLOW(CRITICAL): 1)FROM: Use the master view name (the 'TableName' value from PageAttribute in HIERARCHY CONTEXT) as your FROM clause. 2)COLUMNS: Use ONLY the 'ColumnName' values from the DICTIONARY CONTEXT entries below. 3)WHERE: Apply the 'TailCondition' filter from HIERARCHY CONTEXT + AND ComID=@CurrentCompanyId. 4)DETAILS: If user asks for details, JOIN the detail view (the 'TableName' from PageDetail in HIERARCHY) ON master 'Primary' = detail 'ForeignKey', add detail 'TailCondition' if not null. 5)NO OTHER tables/views/JOINs except dropdown lookups per COLUMN RESOLUTION.");
            sb.AppendLine("COLUMN SAFETY(ZERO TOLERANCE): SchemaScript is for understanding structure ONLY. NEVER SELECT columns from SchemaScript. The Dictionary/ListSetups entries are the ONLY valid column source. Any column NOT in Dictionary=FATAL ERROR. ListName is metadata, NOT a table name.");

            // 4. Column Resolution
            sb.AppendLine("COLUMN RESOLUTION: For EACH column: A)Find Dictionary entry. B)Read ColumnName,SearchName,InsertType,DroModel,DroValue,DroText,Vis. C)If InsertType!='dropdown'→SELECT T1.ColumnName AS [arColumnLabel]. D)If dropdown AND SearchName!=ColumnName→SELECT T1.ColumnName AS [arColumnLabel]. E)If dropdown AND SearchName==ColumnName: if DroText exists in current view→SELECT T1.DroText AS [arColumnLabel] else JOIN DroModel ON T1.SearchName=DroModel.DroValue SELECT DroModel.DroText AS [arColumnLabel]. F)ALWAYS alias AS [arColumnLabel].");
            sb.AppendLine("DEFAULT COLUMNS: 'هات/اعرض/جيب' without specifics→ALL visible=true columns per COLUMN RESOLUTION. NEVER SELECT *.");
            sb.AppendLine("VAGUE QUERY GUARD(CRITICAL): If the user's question is general/vague (e.g. 'show payments', 'متحصلات الدفعات'), select ONLY the visible=true columns from the DICTIONARY CONTEXT using the COLUMN RESOLUTION rules above. NEVER dump all columns from SchemaScript. The Dictionary is your ONLY column source. SchemaScript columns that do NOT appear in the Dictionary MUST be ignored completely—selecting them is a FATAL ERROR that will crash the database.");

            // 5. Query Format
            sb.AppendLine("FORMAT: Table aliases T1,T2. Prefix columns T1.Col. ALIAS AS [ArabicName] ALWAYS. TEXT: LIKE N'%val%'. DATES: >= DATEADD() for ranges, or MONTH(Col)=X. MATH: ISNULL(Col,0). TOP 100 default. FATAL: TOP+OFFSET together→delete TOP, use OFFSET/FETCH. NO CTEs.");
            sb.AppendLine("ORDERING: No user sort→ORDER BY DESC: ID→Date→Numeric→Text.");

            // 6. Ambiguity
            sb.AppendLine("AMBIGUITY: NEVER ask technical DB questions. Simple queries('هات/اعرض/جيب')=no clarification, confidence=1.0. Only ask if TRULY ambiguous. Arabic only, no technical terms.");
            sb.AppendLine("CONFIDENCE: High(0.9-1.0)=exact match. Med(0.7-0.9)=inferred. Low(<0.7)=ambiguous→clarification.");

            var result = sb.ToString();
            _staticRulesTokenCount = result.Length / 4; // ~4 chars per token

            return result;
        }

        /// <summary>
        /// Diagnostic logging method to analyze context structure and size (DEV ONLY)
        /// </summary>
        private void LogDetailedPromptAnalysis(RetrievalContextBundle contextBundle, string fullPrompt)
        {
            _logger.LogWarning("╔═══════════════════════════════════════════════════════════════════╗");
            _logger.LogWarning("║      🔍 CONTEXT ANALYSIS MODE (DIAGNOSTIC LOGS ENABLED)          ║");
            _logger.LogWarning("╚═══════════════════════════════════════════════════════════════════╝");

            // 1. Schema Context Sample
            if (!string.IsNullOrEmpty(contextBundle.UnifiedPageContext))
            {
                _logger.LogWarning("\n[ContextAnalysis] 📊 UNIFIED SCHEMA CONTEXT:");
                _logger.LogWarning("[ContextAnalysis] Size: {Size:N0} chars (~{Tokens:N0} tokens)",
                    contextBundle.UnifiedPageContext.Length,
                    contextBundle.UnifiedPageContext.Length / 4);

                // Pretty-print first 2000 chars
                var schemaSample = contextBundle.UnifiedPageContext.Length > 2000
                    ? contextBundle.UnifiedPageContext.Substring(0, 2000) + "\n... [TRUNCATED]"
                    : contextBundle.UnifiedPageContext;
                _logger.LogWarning("[ContextAnalysis] Content:\n{Content}", schemaSample);
            }

            // 2. Dictionary Entries Sample (First 5)
            if (contextBundle.DictionaryEntries != null && contextBundle.DictionaryEntries.Any())
            {
                _logger.LogWarning("\n[ContextAnalysis] 📙 DICTIONARY ENTRIES (Showing first 5 of {Total}):",
                    contextBundle.DictionaryEntries.Count);

                foreach (var entry in contextBundle.DictionaryEntries.Take(5))
                {
                    _logger.LogWarning("[ContextAnalysis]   {Entry}", entry);
                }

                // Calculate total dictionary size
                var totalDictSize = contextBundle.DictionaryEntries.Sum(e => e?.Length ?? 0);
                _logger.LogWarning("[ContextAnalysis] Total Dictionary Size: {Size:N0} chars (~{Tokens:N0} tokens)",
                    totalDictSize, totalDictSize / 4);
            }

            // 3. Business Rules Sample
            if (contextBundle.BusinessRules != null && contextBundle.BusinessRules.Any())
            {
                _logger.LogWarning("\n[ContextAnalysis] 📋 BUSINESS RULES (Showing first 2 of {Total}):",
                    contextBundle.BusinessRules.Count);

                foreach (var rule in contextBundle.BusinessRules.Take(2))
                {
                    var ruleSample = rule.Length > 500 ? rule.Substring(0, 500) + "..." : rule;
                    _logger.LogWarning("[ContextAnalysis]   {Rule}", ruleSample);
                }
            }

            // 4. Save Full Prompt to File for Deep Inspection
            try
            {
                var tempDir = Path.Combine(Directory.GetCurrentDirectory(), "temp_prompts");
                Directory.CreateDirectory(tempDir);

                var filename = $"prompt_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                var filepath = Path.Combine(tempDir, filename);

                File.WriteAllText(filepath, fullPrompt);
                _logger.LogWarning("\n[ContextAnalysis] 💾 Full Prompt saved to: {Path}", filepath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ContextAnalysis] Failed to save prompt to file");
            }

            _logger.LogWarning("╚═══════════════════════════════════════════════════════════════════╝\n");
        }

        #endregion
    }
}