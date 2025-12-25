using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
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
        private readonly IGeminiCacheManager _cacheManager;

        // ============================================================================
        // GEMINI EXPLICIT CACHE OPTIMIZATION
        // Static rules are cached on Google's servers for 75% cost discount
        // and 3-5 second latency reduction per request
        // ============================================================================
        private static readonly Lazy<string> _cachedStaticRules = new Lazy<string>(() => BuildStaticSystemRules());
        private static int _staticRulesTokenCount = 0;

        public SqlGenerationService(
            IChatCompletionService chatService,
            ILogger<SqlGenerationService> logger,
            IGeminiCostTracker costTracker,
            IGeminiCacheManager cacheManager)
        {
            _chatService = chatService;
            _logger = logger;
            _costTracker = costTracker;
            _cacheManager = cacheManager;
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

            // 1. Compressed Rules (Prompt Diet)
            sb.AppendLine("ROLE: Expert ERP Database Architect. GOAL: Convert user requests into SAFE T-SQL SELECT queries with MANDATORY ARABIC ALIASES. OUTPUT: T-SQL (markdown) OR CLARIFICATION_NEEDED. Never both.");
            sb.AppendLine("SECURITY(ABSOLUTE):1)SELECT ONLY. FORBIDDEN: INSERT,UPDATE,DELETE,MERGE,DROP,ALTER,CREATE,TRUNCATE,EXEC,sp_,xp_,GRANT,REVOKE. If requested→Output ONLY: REFUSED: عذراً، هذا النظام مخصص للاستعلامات فقط (SELECT). يرجى التواصل مع مسؤول النظام. 2)MULTI-TENANT: EVERY query MUST include AND ComID=@CurrentCompanyId. No exceptions.");
            sb.AppendLine("DATA SOURCES(NO HALLUCINATION):3)ALLOWED TABLES ONLY: Master=PageAttribute.TableName, Detail=PageDetail.TableName, Lookup=ListSetups.DroModel ONLY if InsertType='dropdown'. Any other table/view=FORBIDDEN. 4)COLUMN SOURCE OF TRUTH: Resolve columns ONLY via ListSetups. Match arColumnLabel/enColumnLabel. If not found→CLARIFICATION_NEEDED. NEVER GUESS.");
            sb.AppendLine("SYSTEM CYCLE:5)Identify Page from HIERARCHY. 6)FROM=Master. 7)If details requested→JOIN Detail ON PageAttribute.Primary=PageDetail.ForeignKey.");
            sb.AppendLine("COLUMN RESOLUTION(ALGORITHM):8)For EVERY column: A)find ListSetups entry. B)read ColumnName,SearchName,InsertType,DroModel,DroValue,DroText. C)Logic: InsertType!=dropdown→select ColumnName(NO JOIN). InsertType=dropdown: if SearchName!=ColumnName→select ColumnName(NO JOIN). if SearchName==ColumnName: if DroText exists in current view→select it(NO JOIN) else JOIN DroModel ON SearchName=DroValue select DroText. D)CRITICAL: ALIAS with [arColumnLabel] (AS [العربية]). 9)All columns/show table→NEVER SELECT *. Select ONLY Visible=1 and resolve EACH column using rule(8).");
            sb.AppendLine("QUERY RULES:10)STRICT FORMAT: Alias Tables(T1..), Prefix Columns(T1.Col), and ALIAS OUTPUTS (AS [ArabicName]) ALWAYS. 11)TEXT: LIKE N'%Value%'. 12)DATES: MONTH(DateCol)=X if year not specified. 13)MATH: ISNULL(Column,0). 14)NO AGG→TOP 100. 15)NO CTEs EVER.");
            sb.AppendLine("ORDERING:16)If no user order→ORDER BY DESC priority: ID/DocNo→Date→Numeric→Text. If user asks sort→respect.");
            sb.AppendLine("COMMENTS(MANDATORY):17)Every SELECT column and JOIN must have comment: --SRC:Table.Col|LIST:ListName|DRO:DroModel/NA|RULE:Step|IDX:i|MSG:Info. 18)Comma BEFORE comment (e.g., \"Col, -- Msg\").");
            sb.AppendLine("CONFIDENCE:19)≥75%→Generate SQL. 20)<75%→CLARIFICATION_NEEDED:[Arabic question].");

            var result = sb.ToString();
            _staticRulesTokenCount = result.Length / 4; // ~4 chars per token

            return result;
        }
    }
}