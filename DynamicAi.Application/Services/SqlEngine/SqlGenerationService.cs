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
        private readonly IGeminiCostTracker _costTracker;
        private readonly IGeminiCacheManager _cacheManager;
        private readonly IConfiguration _config;
        private readonly IGeminiPageContextCache _geminiPageCache;

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
            IGeminiCostTracker costTracker,
            IGeminiCacheManager cacheManager,
            IGeminiPageContextCache geminiPageCache,
            IConfiguration configuration)
        {
            _chatService = chatService;
            _logger = logger;
            _costTracker = costTracker;
            _cacheManager = cacheManager;
            _geminiPageCache = geminiPageCache;
            _config = configuration;
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

        /// <param name="actionType">نوع الإجراء المقرر من Condenser (New/WrapPrevious/Regenerate)</param>
        public async Task<SqlGenerationResult> GenerateSqlAsync(
            string userQuestion,
            RetrievalContextBundle contextBundle,
            string? previousSql = null,
            QueryActionType actionType = QueryActionType.Regenerate)
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
                promptBuilder.AppendLine("This JSON object represents the complete page structure. The `SchemaScript` property contains the T-SQL CREATE VIEW statement.");
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

            #endregion

            #region STEP 2.6: Mode-Specific Directives (Wrap/Regen)

            // ============================================================================
            // ACTION TYPE DIRECTIVE (From Query Condenser Layer 0.5)
            // ============================================================================
            if (actionType == QueryActionType.WrapPrevious && !string.IsNullOrWhiteSpace(previousSql))
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
            if (_config.GetValue<bool>("Logging:EnableContextAnalysisLogs"))
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
                        CancellationToken.None
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
                result.Reasoning = $"Generated SQL using {actionType} mode based on the provided schema and user constraints.";
            }

            // 5. Append Cache Info to Reasoning
            if (cachedTokens > 0)
            {
                result.Reasoning += $" (⚡ accelerated by Gemini Cache, saved {savings:C6})";
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

            // 1. Compressed Rules (Prompt Diet)
            sb.AppendLine("ROLE: Expert ERP Database Architect. GOAL: Convert user requests into JSON response containing SAFE T-SQL SELECT queries or Clarification requests.");
            sb.AppendLine("OUTPUT FORMAT: You must return a SINGLE JSON object. Markdown code fences ```json ... ``` are optional but preferred.");
            sb.AppendLine("JSON SCHEMA: { \"sql\": \"SELECT...\", \"confidence\": 0.0-1.0, \"reasoning\": \"Brief logic\", \"needs_clarification\": boolean, \"clarification_question\": \"Arabic Question or null\" }");

            sb.AppendLine("SECURITY(ABSOLUTE):1)SELECT ONLY. FORBIDDEN: INSERT,UPDATE,DELETE,MERGE,DROP,ALTER,CREATE,TRUNCATE,EXEC,sp_,xp_,GRANT,REVOKE. If requested→Output SQL='' and reasoning='REFUSED: Security Violation'. 2)MULTI-TENANT: EVERY query MUST include AND ComID=@CurrentCompanyId. No exceptions.");
            sb.AppendLine("DATA SOURCES(NO HALLUCINATION):3)ALLOWED TABLES ONLY: Master=PageAttribute.TableName, Detail=PageDetail.TableName, Lookup=ListSetups.DroModel ONLY if InsertType='dropdown'. Any other table/view=FORBIDDEN. 4)COLUMN SOURCE OF TRUTH: Resolve columns ONLY via ListSetups. Match arColumnLabel/enColumnLabel. If not found→needs_clarification=true. NEVER GUESS.");
            sb.AppendLine("SYSTEM CYCLE:5)Identify Page from HIERARCHY. 6)FROM=Master. 7)If details requested→JOIN Detail ON PageAttribute.Primary=PageDetail.ForeignKey.");
            sb.AppendLine("COLUMN RESOLUTION(ALGORITHM):8)For EVERY column: A)find ListSetups entry. B)read ColumnName,SearchName,InsertType,DroModel,DroValue,DroText,Vis. C)Logic: InsertType!=dropdown→select ColumnName(NO JOIN). InsertType=dropdown: if SearchName!=ColumnName→select ColumnName(NO JOIN). if SearchName==ColumnName: if DroText exists in current view→select it(NO JOIN) else JOIN DroModel ON SearchName=DroValue select DroText. D)CRITICAL: ALIAS with [arColumnLabel] (AS [العربية]). 9)All columns/show table→NEVER SELECT *. Select ONLY columns where Vis=true and resolve EACH column using rule(8).");
            sb.AppendLine("QUERY RULES:10)STRICT FORMAT: Alias Tables(T1..), Prefix Columns(T1.Col), and ALIAS OUTPUTS (AS [ArabicName]) ALWAYS. 11)TEXT: LIKE N'%Value%'. 12)DATES: MONTH(DateCol)=X if year not specified. 13)MATH: ISNULL(Column,0). 14)PAGING: FATAL ERROR if TOP used with OFFSET. If OFFSET is required -> YOU MUST DELETE 'TOP'. Usage: [ORDER BY ... OFFSET X ROWS FETCH NEXT Y ROWS ONLY]. If no offset -> Use TOP 100. 15)NO CTEs EVER.");
            sb.AppendLine("ORDERING:16)If no user order→ORDER BY DESC priority: ID/DocNo→Date→Numeric→Text. If user asks sort→respect.");
            sb.AppendLine("COMMENTS(MANDATORY):17)Every SELECT column and JOIN must have comment: --SRC:Table.Col|LIST:ListName|DRO:DroModel/NA|RULE:Step|IDX:i|MSG:Info. 18)Comma BEFORE comment (e.g., \"Col, -- Msg\").");
            sb.AppendLine("AMBIGUITY HANDLING:19)NEVER ask technical database questions. 20)SIMPLE QUERIES NEED NO CLARIFICATION: 'هات آخر X'/'اعرض'/'جيب' WITHOUT column names→return ALL Visible=1 columns(rule 9) and confidence=1.0. NEVER ask about notes/optional columns. 21)Only ask clarification if TRULY ambiguous AND business-critical. 22)If must ask→needs_clarification=true, clarification_question='one simple Arabic question'. 23)FORBIDDEN in clarification: technical terms.");
            sb.AppendLine("CONFIDENCE RULES: High(0.9-1.0)=Exact schema match+standard query. Med(0.7-0.9)=Inferred standard logic. Low(<0.7)=Ambiguous or missing columns (triggers clarification).");

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