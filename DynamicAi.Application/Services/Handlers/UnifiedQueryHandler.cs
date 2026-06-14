using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DynamicAi.Application.Exceptions;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Prompts;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DynamicAi.Application.Services.Handlers;

/// <summary>
/// The single, unified handler that replaces SqlIntentHandler, ChatIntentHandler, and PredictIntentHandler.
/// It uses the PromptTemplateEngine and a Master Prompt to route and resolve intents in one LLM call.
/// </summary>
public class UnifiedQueryHandler : IIntentHandler
{
    private readonly IPromptTemplateEngine _promptEngine;
    private readonly IChatCompletionService _chatService;
    private readonly IRagRetrievalService _retrievalService;
    private readonly ISqlValidatorService _validatorService;
    private readonly ISqlSanitizerService _sanitizerService;
    private readonly ISqlRewriterService _rewriterService;
    private readonly ISandboxedSqlExecutor _executionService;
    private readonly IQueryCacheService _queryCacheService;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly IDbSchemaProvider _schemaProvider;
    private readonly IColumnSynonymRepository _synonymRepository;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<UnifiedQueryHandler> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly bool _compactDirectedContext;

    // TSql160Parser is thread-safe for parsing — instantiated once and reused across all requests.
    private static readonly TSqlParser _sqlParser = new TSql160Parser(false);

    public UnifiedQueryHandler(
        IPromptTemplateEngine promptEngine,
        IChatCompletionService chatService,
        IRagRetrievalService retrievalService,
        ISqlValidatorService validatorService,
        ISqlSanitizerService sanitizerService,
        ISqlRewriterService rewriterService,
        ISandboxedSqlExecutor executionService,
        IQueryCacheService queryCacheService,
        ILlmUsageTracker usageTracker,
        IDbSchemaProvider schemaProvider,
        IColumnSynonymRepository synonymRepository,
        IMemoryCache memoryCache,
        ILogger<UnifiedQueryHandler> logger,
        IConfiguration configuration)
    {
        _promptEngine = promptEngine;
        _chatService = chatService;
        _retrievalService = retrievalService;
        _validatorService = validatorService;
        _sanitizerService = sanitizerService;
        _rewriterService = rewriterService;
        _executionService = executionService;
        _queryCacheService = queryCacheService;
        _usageTracker = usageTracker;
        _schemaProvider = schemaProvider;
        _synonymRepository = synonymRepository;
        _memoryCache = memoryCache;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        _compactDirectedContext = configuration.GetValue<bool>("Features:DirectedContextCompactEnabled", false);
        _logger.LogInformation("[UnifiedHandler] 🗜️ DirectedContextCompact = {Mode}",
            _compactDirectedContext ? "ENABLED (Pipe-Delimited)" : "DISABLED (Raw JSON)");
    }

    public IntentType[] SupportedIntents => new[]
    {
        IntentType.DataRetrieval,
        IntentType.Analysis,
        IntentType.Prediction,
        IntentType.Chat,
        IntentType.Unknown
    };

    public bool CanHandle(IntentType intent) => SupportedIntents.Contains(intent);

    public async Task<QueryExecutionResult> HandleAsync(
        string userQuestion,
        IntentClassificationResult intentResult,
        IntentHandlerContext? context = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[UnifiedHandler] Starting Unified Pipeline for Question: \"{Question}\"", userQuestion);
        var pipelineStart = DateTime.UtcNow;
        var tenantId = context?.TenantId ?? "default";
        var companyId = context?.CompanyId;

        // --- PHASE 0: CACHE BYPASS ---
        if (!string.IsNullOrWhiteSpace(context?.CachedSql))
        {
            _logger.LogInformation("[UnifiedHandler] ⚡ GLOBAL CACHE HIT (ID: {CacheId}). Bypassing RAG and LLM completely!", context.CachedCacheId);
            
            try
            {
                // Dummy bundle so the executor knows which PageId this was tied to.
                var dummyRetrieval = new RetrievalContextBundle { PageId = context.CachedPageId ?? 0 };
                
                var cachedSqlResult = await ExecuteSqlPipelineAsync(context.CachedSql, tenantId, companyId, dummyRetrieval, context, cancellationToken, schemaPrefetchTask: null);
                cachedSqlResult.IsCached = true;
                cachedSqlResult.CacheId = context.CachedCacheId; // 🆔 Carry original Qdrant Entry ID to result
                
                _logger.LogInformation("[UnifiedHandler] ✓ Cached result enriched with CacheId: {CacheId}", cachedSqlResult.CacheId);
                
                // A cache hit implies it's either DataRetrieval or PredictionWithDataRetrieval. Use recorded CachedIntent if available.
                var defaultIntent = intentResult.Intent == IntentType.Unknown ? IntentType.DataRetrieval.ToString() : intentResult.Intent.ToString();
                cachedSqlResult.Intent = !string.IsNullOrEmpty(context.CachedIntent) ? context.CachedIntent : defaultIntent;
                
                if (cachedSqlResult.QueryInsights == null) cachedSqlResult.QueryInsights = new QueryInsightData();
                if (cachedSqlResult.QueryInsights.RetrievedContext == null) cachedSqlResult.QueryInsights.RetrievedContext = new List<string>();
                cachedSqlResult.QueryInsights.RetrievedContext.Add("⚡ Served directly from Global Query Cache");
                
                return cachedSqlResult;
            }
            catch (Exception cacheEx)
            {
                // 🛡️ Cache is corrupt or SQL invalid! Delete it and let it fall through to the LLM
                _logger.LogWarning(cacheEx, 
                    "[UnifiedHandler] ⚠️ CACHE FALLBACK: Cached SQL failed ({Error}). " +
                    "Invalidating cache and falling through to full LLM pipeline.", 
                    cacheEx.Message);

                _ = Task.Run(async () =>
                {
                    try 
                    { 
                        if (context?.PrecomputedEmbedding != null)
                        {
                            await _queryCacheService.DeleteSimilarCacheAsync(context.PrecomputedEmbedding, tenantId, companyId ?? 0); 
                        }
                    }
                    catch { /* Swallow exception during fire-and-forget delete */ }
                });
            }
        }

        // Phase 1: Context Resolution (RAG vs Direct Injection)
        RetrievalContextBundle retrievalResult;
        ShippedPageContext? finalContext = null;

        if (context?.InjectedContext != null)
        {
            _logger.LogInformation("[UnifiedHandler] 🚀 DIRECTED CONTEXT DETECTED! Bypassing RAG Service completely.");
            finalContext = context.InjectedContext;
            retrievalResult = new RetrievalContextBundle 
            { 
                PageId = finalContext.PageId,
                StructuredContext = finalContext 
            };
        }
        else
        {
            _logger.LogInformation("[UnifiedHandler] Layer 1: Retrieving Context from Qdrant...");
            retrievalResult = await _retrievalService.RetrieveContextAsync(
                userQuestion,
                tenantId,
                companyId,
                precomputedEmbedding: context?.PrecomputedEmbedding,
                explicitPageId: context?.ExplicitPageId,
                ct: cancellationToken);
            
            finalContext = retrievalResult.StructuredContext;
        }

        int currentPageId = finalContext?.PageId ?? 0;

        // ═══════════════════════════════════════════════════════════════════
        // 🚀 PARALLEL SCHEMA PREFETCH (Fire-and-Hold)
        // ═══════════════════════════════════════════════════════════════════
        // إذا الطلب جاء مع ConnectionString مخصص → ابدأ جلب الـ Schema الآن
        // بدون await (لا نتوقف هنا). المهمة ستعمل في الخلفية بينما نبني الـ Prompt
        // ونستدعي الـ LLM. لما نوصل للـ Sanitizer، نعمل await على المهمة دي —
        // في الغالب ستكون خلصت بالفعل لأن الـ LLM أبطأ بكتير من DB.
        Task<Dictionary<string, HashSet<string>>>? schemaPrefetchTask = null;

        if (!string.IsNullOrEmpty(context?.ConnectionString) && finalContext != null)
        {
            var viewNames = new List<string?> { finalContext.MasterSetup?.TableName }
                .Concat(finalContext.DetailSetups?.Select(d => d.TableName) ?? [])
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToList();

            var tailConditionTables = ExtractTablesFromTailConditions(finalContext);
            var addedFromTail = tailConditionTables.Where(t => !viewNames.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            viewNames.AddRange(addedFromTail);

            if (addedFromTail.Any())
                _logger.LogInformation(
                    "[UnifiedHandler] 🔍 TailCondition AST scan found {Count} additional table(s) to prefetch: [{Tables}]",
                    addedFromTail.Count, string.Join(", ", addedFromTail));
            else
                _logger.LogInformation("[UnifiedHandler] ✅ TailCondition AST scan: no extra tables detected (no subqueries in conditions).");

            if (viewNames.Any())
            {
                _logger.LogInformation(
                    "[UnifiedHandler] 🔄 PARALLEL: Starting schema prefetch for {Count} views in background (connection-scoped).",
                    viewNames.Count);

                // لا نستخدم Task.Run لأن GetSchemaForViewsAsync هي async بالفعل.
                // نستدعيها مباشرة بدون await — هتبدأ وترجع Task نمسك فيه.
                schemaPrefetchTask = _schemaProvider.GetSchemaForViewsAsync(
                    context.ConnectionString,
                    viewNames,
                    cancellationToken);
            }
        }

        // --- PHASE 1.5: SECONDARY CACHE CHECK (Post-RAG) ---
        // If the global check missed because PageId was unknown, we check again now that we have the resolved PageId.
        if (string.IsNullOrWhiteSpace(context?.CachedSql) && currentPageId > 0)
        {
            _logger.LogInformation("[UnifiedHandler] Layer 1.5: Checking Page-Specific Cache for PageID {PageId}...", currentPageId);
            var pageCachedResult = await _queryCacheService.TryGetCachedSqlAsync(
                context?.PrecomputedEmbedding ?? new float[0], // Reuse precomputed embedding
                tenantId,
                companyId ?? 1,
                currentPageId,
                cancellationToken);

            if (pageCachedResult != null)
            {
                _logger.LogInformation("[UnifiedHandler] ⚡ SECONDARY CACHE HIT (PageID: {PageId}). Bypassing LLM!", currentPageId);
            var cachedSqlResult = await ExecuteSqlPipelineAsync(pageCachedResult.Sql, tenantId, companyId, retrievalResult, context, cancellationToken, schemaPrefetchTask: null);
                cachedSqlResult.IsCached = true;
                cachedSqlResult.CacheId = pageCachedResult.CacheId.ToString();
                cachedSqlResult.Intent = pageCachedResult.Intent;
                return cachedSqlResult;
            }
        }

        // Retrieve approved synonyms with 120-minute memory cache
        var approvedSynonyms = new List<ColumnSynonym>();
        if (currentPageId > 0)
        {
            var cacheKey = $"synonyms_page_{currentPageId}";
            if (!_memoryCache.TryGetValue(cacheKey, out approvedSynonyms) || approvedSynonyms == null)
            {
                approvedSynonyms = await _synonymRepository.GetApprovedByPageAsync(currentPageId, cancellationToken);
                _memoryCache.Set(cacheKey, approvedSynonyms, TimeSpan.FromMinutes(120));
            }
        }
        
        string schemaContent = finalContext != null
            ? (_compactDirectedContext
                ? CompressPageContextForPrompt(finalContext, approvedSynonyms)
                : JsonSerializer.Serialize(finalContext, _jsonOptions))
            : string.Empty;

        string rulesContent = finalContext?.BusinessRules?.Any() == true
            ? string.Join("\n", finalContext.BusinessRules.Select((r, i) => $"[Rule {i+1}]\n{r}"))
            : string.Empty;

        // Phase 2: Render Master Prompt
        _logger.LogInformation("[UnifiedHandler] Layer 2: Rendering Master Prompt...");
        
        var variables = new Dictionary<string, object>
        {
            { "user_question", userQuestion },
            { "company_id", companyId?.ToString() ?? "0" },
            { "current_date", DateTime.Now.ToString("yyyy-MM-dd") },
            { "current_year", DateTime.Now.Year.ToString() }
        };

        // --- Build TailCondition Block ---
        var (tailEntries, tailBlock) = BuildTailConditionBlock(finalContext, context);
        if (!string.IsNullOrEmpty(tailBlock))
        {
            variables["tail_condition"] = tailBlock;
        }

        if (!string.IsNullOrEmpty(schemaContent))
        {
            variables["page_attributes_details_listsetups"] = schemaContent;
        }

        if (retrievalResult.DictionaryEntries.Any())
        {
            variables["legacy_dictionary_context"] = string.Join("\n", retrievalResult.DictionaryEntries);
        }

        if (!string.IsNullOrEmpty(rulesContent))
        {
            variables["business_rules"] = rulesContent;
        }

        _logger.LogInformation("[UnifiedHandler] Context Strategy: " +
            "Structured={HasStructured} ({SchemaSize} chars), " +
            "Legacy={HasLegacy} ({LegacyCount} entries)", 
            !string.IsNullOrEmpty(schemaContent), schemaContent.Length, 
            retrievalResult.DictionaryEntries.Any(), retrievalResult.DictionaryEntries.Count);

        // --- Build Conversation History Block ---
        var historyBlock = BuildConversationHistoryBlock(context, currentPageId);
        if (!string.IsNullOrEmpty(historyBlock))
        {
            variables["conversation_history"] = historyBlock;
        }

        var renderedPrompt = await _promptEngine.RenderAsync("unified_master", variables);

        // Phase 3: Call LLM
        _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║              [Layer 3: GEMINI LLM] - CALLING MODEL                     ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");
        
        // 🛰️ LOG FULL PROMPT
        _logger.LogInformation("[UnifiedHandler] 🛰️ FULL RENDERED PROMPT START:\n{Prompt}\n[UnifiedHandler] 🛰️ FULL RENDERED PROMPT END", renderedPrompt);
        
        _logger.LogInformation("[UnifiedHandler] Layer 3: Calling Gemini LLM...");
        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(renderedPrompt);
        // LLM prompt includes user input implicitly via unified_master template, but adding user message explicitly is also fine.
        // Actually, unified_master already has "TASK Q: {{user_question}}". So we just send userQuestion.
        chatHistory.AddUserMessage(userQuestion);

        var llmStart = DateTime.UtcNow;
        var response = await _chatService.GetChatMessageContentAsync(chatHistory, cancellationToken: cancellationToken);
        var llmDuration = (DateTime.UtcNow - llmStart).TotalMilliseconds;
        
        var rawResponse = response.Content ?? string.Empty;
        _logger.LogInformation("[UnifiedHandler] ✓ LLM Responded in {Duration:F0}ms", llmDuration);
        
        // 🛰️ LOG RAW RESPONSE BEFORE PARSING
        _logger.LogInformation("[UnifiedHandler] 🛰️ RAW LLM RESPONSE START:\n{RawResponse}\n[UnifiedHandler] 🛰️ RAW LLM RESPONSE END", rawResponse);

        // Track LLM Usage
        int estimatedInputTokens = (renderedPrompt.Length + userQuestion.Length) / 4;
        int estimatedOutputTokens = rawResponse.Length / 4;
        
        await _usageTracker.TrackLlmCallAsync(
            LlmOperationType.SqlGeneration,
            requestId: context?.QueryLogId.ToString() ?? Guid.NewGuid().ToString(),
            inputTokens: estimatedInputTokens,
            outputTokens: estimatedOutputTokens,
            cachedTokens: 0,
            tenantId: tenantId,
            companyId: companyId,
            cancellationToken: cancellationToken);

        // Phase 4: Parse Unified JSON
        var parsedResponse = ExtractAndParseJson(rawResponse);
        if (parsedResponse == null)
        {
            _logger.LogWarning("[UnifiedHandler] 🔄 JSON Parse failed. Asking LLM to fix unescaped quotes or syntax...");
            chatHistory.AddAssistantMessage(rawResponse);
            chatHistory.AddUserMessage(
                "Your previous response contained invalid JSON (likely due to unescaped quotation marks inside string values like 'explanation' or 'sql', or other syntax errors). " + 
                "Please FIX the JSON and return EXACTLY the same content but properly escaped. " + 
                "Do NOT include any markdown formatting, only raw valid JSON.");
            
            var retryStart = DateTime.UtcNow;
            var retryResponse = await _chatService.GetChatMessageContentAsync(chatHistory, cancellationToken: cancellationToken);
            _logger.LogInformation("[UnifiedHandler] ✅ JSON Fix LLM Retry responded in {Duration:F0}ms", (DateTime.UtcNow - retryStart).TotalMilliseconds);
            
            rawResponse = retryResponse.Content ?? string.Empty;
            parsedResponse = ExtractAndParseJson(rawResponse);
            
            if (parsedResponse == null)
            {
                _logger.LogError("[UnifiedHandler] ❌ Failed to parse unified JSON response even after retry. Raw was: {Raw}", rawResponse);
                return new QueryExecutionResult
                {
                    Data = new[] { new { Message = "عذراً، لم أتمكن من صياغة الإجابة بالشكل الصحيح.", Type = "Error" } },
                    QueryLogId = context?.QueryLogId,
                    ErrorMessage = "Failed to parse JSON from LLM"
                };
            }
            _logger.LogInformation("[UnifiedHandler] ✅ JSON successfully parsed after self-healing.");
        }

        _logger.LogInformation("[UnifiedHandler] LLM Intent: {Intent}, Confidence: {Conf}", parsedResponse.Intent, parsedResponse.Confidence);

        if (parsedResponse.SchemaLinking != null && parsedResponse.SchemaLinking.Any())
        {
            _logger.LogInformation("[UnifiedHandler] ═══════════════ 🧠 SCHEMA LINKING ANALYSIS ═══════════════");
            foreach (var entry in parsedResponse.SchemaLinking)
            {
                _logger.LogInformation("[UnifiedHandler]   • \"{UserTerm}\" ──➔ {MappedTo} ({Label}) | Reason: {Reason}", 
                    entry.UserTerm, entry.MappedToColumn, entry.ArLabel, entry.Reason);
            }
            _logger.LogInformation("[UnifiedHandler] ═══════════════════════════════════════════════════════════");

            // 🔤 Auto-capture schema linking synonyms (fire-and-forget)
            if (currentPageId > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        foreach (var entry in parsedResponse.SchemaLinking)
                        {
                            if (string.IsNullOrWhiteSpace(entry.UserTerm) ||
                                string.IsNullOrWhiteSpace(entry.MappedToColumn)) continue;

                            var parts = entry.MappedToColumn.Split('.');
                            var columnName = parts.Length > 1 ? parts[1] : parts[0];
                            var tableName = ResolveAliasToTable(entry.MappedToColumn, finalContext);
                            if (string.IsNullOrEmpty(tableName)) continue;

                            await _synonymRepository.UpsertAsync(new ColumnSynonym
                            {
                                PageId = currentPageId,
                                TableName = tableName,
                                ColumnName = columnName,
                                UserTerm = entry.UserTerm.Trim(),
                                Language = ContainsArabic(entry.UserTerm) ? "ar" : "en"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        // Swallow and log to avoid blocking the main query execution path
                        System.Diagnostics.Debug.WriteLine($"Failed to auto-capture synonym: {ex.Message}");
                    }
                });
            }
        }

        // Phase 5: Execute Route
        if (parsedResponse.IsSqlIntent)
        {
            // --- SELF-HEALING RETRY LOOP ---
            const int maxRetries = 2;
            string currentSql = parsedResponse.Content.Sql;
            string? lastError = null;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        _logger.LogWarning("[UnifiedHandler] 🔄 Self-Healing Attempt {Attempt}/{Max}. Asking LLM to fix: {Error}",
                            attempt, maxRetries, lastError);

                        // Send the error back to the LLM as a correction message
                        chatHistory.AddAssistantMessage(rawResponse);
                        chatHistory.AddUserMessage(
                            $"The SQL query you generated failed with the following error:\n" +
                            $"{lastError}\n\n" +
                            $"Please FIX the SQL query. Return your response in the exact same JSON format as before. " +
                            $"Do NOT use columns or tables that don't exist in the schema.");

                        var retryStart = DateTime.UtcNow;
                        var retryResponse = await _chatService.GetChatMessageContentAsync(chatHistory, cancellationToken: cancellationToken);
                        var retryDuration = (DateTime.UtcNow - retryStart).TotalMilliseconds;
                        rawResponse = retryResponse.Content ?? string.Empty;
                        _logger.LogInformation("[UnifiedHandler] ✅ LLM Retry responded in {Duration:F0}ms", retryDuration);

                        var retryParsed = ExtractAndParseJson(rawResponse);
                        if (retryParsed == null || !retryParsed.IsSqlIntent || string.IsNullOrWhiteSpace(retryParsed.Content?.Sql))
                        {
                            _logger.LogWarning("[UnifiedHandler] ❌ LLM retry did not produce valid SQL. Giving up.");
                            break;
                        }
                        currentSql = retryParsed.Content.Sql;
                        parsedResponse = retryParsed; // update for metadata
                    }

                    var sqlResult = await ExecuteSqlPipelineAsync(currentSql, tenantId, companyId, retrievalResult, context, cancellationToken, schemaPrefetchTask);
                    sqlResult.ConfidenceScore = (float?)parsedResponse.Confidence;
                    if (sqlResult.QueryInsights == null) sqlResult.QueryInsights = new QueryInsightData { RetrievedContext = new List<string>() };
                    sqlResult.QueryInsights.RetrievedContext?.Add($"🤖 Intent Detected: {parsedResponse.Intent}");
                    sqlResult.QueryInsights.Reasoning = parsedResponse.Content.Explanation;
                    sqlResult.Intent = parsedResponse.Intent;

                    if (attempt > 0)
                    {
                        sqlResult.QueryInsights.RetrievedContext?.Add($"🔄 Self-healed after {attempt} retry(ies)");
                        _logger.LogInformation("[UnifiedHandler] ✅ Self-Healing SUCCESS on attempt {Attempt}", attempt);
                    }

                    return sqlResult;
                }
                catch (SqlSanitizationException sanitizationEx)
                {
                    lastError = sanitizationEx.Message;
                    _logger.LogWarning("[UnifiedHandler] 🛡️ Sanitizer rejected SQL: {Error}", lastError);
                    if (attempt >= maxRetries)
                    {
                        _logger.LogError("[UnifiedHandler] ❌ Max retries reached after SqlSanitizationException.");
                        return new QueryExecutionResult
                        {
                            ErrorMessage = $"CLARIFICATION_REQUIRED: عذراً، الاستعلام الذي تم توليده يحتوي على أعمدة أو جداول غير موجودة. حاول إعادة صياغة سؤالك.",
                            QueryLogId = context?.QueryLogId,
                            Intent = parsedResponse.Intent
                        };
                    }
                }
                catch (SqlException dbEx)
                {
                    lastError = $"Database error: {dbEx.Message}";
                    _logger.LogWarning("[UnifiedHandler] 💾 Database rejected SQL: {Error}", dbEx.Message);
                    if (attempt >= maxRetries)
                    {
                        _logger.LogError("[UnifiedHandler] ❌ Max retries reached after SqlException.");
                        throw; // Let the orchestrator handle it
                    }
                }
            }
        }
        else if (parsedResponse.IsPredictionIntent || parsedResponse.IsChatIntent)
        {
            var resultType = parsedResponse.IsPredictionIntent ? "Prediction" : "Chat";
            return new QueryExecutionResult
            {
                Data = new[] { new { Message = parsedResponse.Content.Message, Type = resultType, Explanation = parsedResponse.Content.Explanation } },
                QueryLogId = context?.QueryLogId,
                ConfidenceScore = (float?)parsedResponse.Confidence,
                IsCached = false,
                QueryInsights = new QueryInsightData { Reasoning = parsedResponse.Content.Explanation },
                Intent = parsedResponse.Intent
            };
        }

        // Fallback for Unknown
        return new QueryExecutionResult
        {
            Data = new[] { new { Message = "عذراً، لم أتمكن من معالجة طلبك.", Type = "Unknown" } },
            QueryLogId = context?.QueryLogId
        };
    }

    private UnifiedLlmResponse? ExtractAndParseJson(string llmResponse)
    {
        try
        {
            var jsonMatch = Regex.Match(llmResponse, @"```json\s*(.*?)\s*```", RegexOptions.Singleline);
            var jsonString = jsonMatch.Success ? jsonMatch.Groups[1].Value.Trim() : llmResponse.Trim();
            
            // Remove any markdown leftovers if regex missed it
            if (jsonString.StartsWith("```")) jsonString = jsonString.Substring(3);
            if (jsonString.EndsWith("```")) jsonString = jsonString.Substring(0, jsonString.Length - 3);

            return JsonSerializer.Deserialize<UnifiedLlmResponse>(jsonString, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UnifiedHandler] JSON Parse Error");
            return null;
        }
    }

    private async Task<QueryExecutionResult> ExecuteSqlPipelineAsync(
        string rawSql, 
        string tenantId, 
        int? companyId,
        RetrievalContextBundle retrievalResult,
        IntentHandlerContext? context,
        CancellationToken ct,
        Task<Dictionary<string, HashSet<string>>>? schemaPrefetchTask = null)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
        {
             return new QueryExecutionResult
             {
                 ErrorMessage = "Generated SQL was empty.",
                 QueryLogId = context?.QueryLogId
             };
        }

        _logger.LogInformation("[UnifiedHandler] Layer 4: Validating & Expanding SQL...");
        await _validatorService.ValidateSqlAsync(rawSql, tenantId, ct);

        // 🛡️ Schema Sanitization: use pre-fetched targeted schema if available, else global schema
        _logger.LogInformation("[UnifiedHandler] Layer 4.5: Sanitizing SQL against schema...");
        if (schemaPrefetchTask != null)
        {
            // Per-Request Path: await the task that started in parallel with LLM call.
            // المهمة دي ابتدت لما بنينا الـ Prompt، فوقت الـ LLM كانت بتشتغل.
            // في الغالب ستكون خلصت بالفعل (await بيكلف 0ms أو أقل من 1ms).
            // connectionString مطلوب عشان الـ Sanitizer يجيب schema الجداول الإضافية (lookups / JOINs)
            await _sanitizerService.SanitizeAsync(rawSql, tenantId, schemaPrefetchTask, ct, context?.ConnectionString);
        }
        else
        {
            // Default Path: global schema from DefaultConnection (warmed at startup)
            await _sanitizerService.SanitizeAsync(rawSql, tenantId, ct);
        }
        
        // ─── Rebuild typed TailConditionEntry list for the hardening layer ─────────────
        // Same alias assignment logic as in the prompt-building phase (T1=Master, T2/T3=Details).
        var tailEntries = new List<TailConditionEntry>();

        if (retrievalResult.StructuredContext?.MasterSetup != null
            && !string.IsNullOrWhiteSpace(retrievalResult.StructuredContext.MasterSetup.TailCondition))
        {
            tailEntries.Add(new TailConditionEntry
            {
                ViewName  = retrievalResult.StructuredContext.MasterSetup.TableName,
                SqlAlias  = "TM",
                Condition = retrievalResult.StructuredContext.MasterSetup.TailCondition
            });
        }

        foreach (var detail in retrievalResult.StructuredContext?.DetailSetups ?? new())
        {
            if (!string.IsNullOrWhiteSpace(detail.TailCondition))
            {
                tailEntries.Add(new TailConditionEntry
                {
                    ViewName  = detail.TableName,
                    SqlAlias  = "TD",
                    Condition = detail.TailCondition
                });
            }
        }

        _logger.LogInformation(
            "[UnifiedHandler] Layer 4.8: Hardening SQL — verifying {Count} TailCondition entries (Master+Details)...",
            tailEntries.Count);
        var rewrittenResult = await _rewriterService.RewriteAsync(rawSql, tenantId, companyId, tailEntries, ct);
        var finalSql = rewrittenResult.RewrittenSql;
        
        _logger.LogInformation("[UnifiedHandler] Layer 5: Executing final SQL...");
        var executionResult = await _executionService.ExecuteSqlAsync(finalSql, rewrittenResult.Parameters, context?.ConnectionString, ct);
        
        var queryInsights = new List<string>();
        if (retrievalResult.StructuredContext != null)
        {
            int columnsCount = retrievalResult.StructuredContext.MasterSetup.Columns.Count + 
                               retrievalResult.StructuredContext.DetailSetups.Sum(d => d.Columns.Count);
            queryInsights.Add($"📚 {columnsCount} Column Definitions Loaded");
        }
        queryInsights.Add("🤖 Dynamic Generated Query");

        return new QueryExecutionResult
        {
            Data = executionResult,
            QueryLogId = context?.QueryLogId,
            IsCached = false,
            RewrittenSql = finalSql,
            QueryInsights = new QueryInsightData { RetrievedContext = queryInsights },
            PageId = retrievalResult.PageId // Will now correctly report the InjectedContext.PageId
        };
    }

    private (List<TailConditionEntry> Entries, string PromptBlock) BuildTailConditionBlock(ShippedPageContext? finalContext, IntentHandlerContext? context)
    {
        var tailEntries = new List<TailConditionEntry>();
        if (finalContext == null) return (tailEntries, string.Empty);

        // 1. Resolve Master Filter
        if (finalContext.MasterSetup != null && !string.IsNullOrWhiteSpace(finalContext.MasterSetup.TailCondition))
        {
            var cleanCond = finalContext.MasterSetup.TailCondition.Trim();
            if (cleanCond.StartsWith("where", StringComparison.OrdinalIgnoreCase)) cleanCond = cleanCond.Substring(5).Trim();

            tailEntries.Add(new TailConditionEntry
            {
                ViewName  = finalContext.MasterSetup.TableName,
                SqlAlias  = "TM",
                Condition = cleanCond
            });
        }

        // 2. Resolve Detail Filters (alias: TD — applied by hardening layer only when TD is present in the query)
        foreach (var detail in finalContext.DetailSetups ?? new())
        {
            if (!string.IsNullOrWhiteSpace(detail.TailCondition))
            {
                var cleanCond = detail.TailCondition.Trim();
                if (cleanCond.StartsWith("where", StringComparison.OrdinalIgnoreCase)) cleanCond = cleanCond.Substring(5).Trim();

                tailEntries.Add(new TailConditionEntry
                {
                    ViewName  = detail.TableName,
                    SqlAlias  = "TD",
                    Condition = cleanCond
                });
            }
        }

        // 3. Build Prompt Block
        if (tailEntries.Any())
        {
            var promptTail = new System.Text.StringBuilder();

            promptTail.AppendLine("RESERVED ALIASES (FIXED — must be used exactly as shown — cannot be reassigned or reused):");
            promptTail.AppendLine($"  TM → Master view: {finalContext.MasterSetup?.TableName} (always in FROM clause)");

            foreach (var detail in finalContext.DetailSetups ?? new())
            {
                promptTail.AppendLine($"  TD → Detail view: {detail.TableName} (JOIN only when the question requires detail-level columns)");
            }

            promptTail.AppendLine("  T1, T2, T3… → Available for dropdown lookup JOINs only (never for master/detail).");
            promptTail.AppendLine();
            promptTail.AppendLine("MANDATORY FILTERS:");
            foreach (var entry in tailEntries)
            {
                promptTail.AppendLine(
                    $"  • View='{entry.ViewName}' | Alias='{entry.SqlAlias}' → CONDITION: ({entry.Condition}). " +
                    $"Prefix every column in this condition with '{entry.SqlAlias}.' (e.g. {entry.SqlAlias}.ColumnName). " +
                    (entry.SqlAlias == "TD"
                        ? "⚠️ Apply this filter ONLY if you JOIN TD. If TD is not in your query, SKIP this filter entirely."
                        : "Apply this filter unconditionally in the main WHERE clause."));
            }

            promptTail.AppendLine();
            promptTail.AppendLine("DROPDOWN LOOKUP JOINS (if needed per COLUMN RESOLUTION rule E): use T1, T2, T3… starting from T1.");

            // Add Directed Context Metadata if applicable
            if (context?.InjectedContext != null)
            {
                promptTail.AppendLine("\nDIRECTED CONTEXT ACTIVE:");
                promptTail.AppendLine($"- Page: '{finalContext.PageName}'");
                promptTail.AppendLine($"- TM (Master): {finalContext.MasterSetup.TableName}");
                
                foreach (var detail in finalContext.DetailSetups ?? new())
                {
                    promptTail.AppendLine($"- TD (Detail): {detail.TableName} — join only if user needs detail columns");
                }
            }

            _logger.LogInformation("[UnifiedHandler] 🛡️ {Count} TailCondition entries built. Aliases: TM=Master, TD=Detail. Dropdown lookups use T1,T2,T3…",
                tailEntries.Count);

            return (tailEntries, promptTail.ToString().Trim());
        }
        else
        {
            _logger.LogInformation(
                "[UnifiedHandler] ℹ️ No TailConditions. Dropdown lookup aliases start from T1.");
            return (tailEntries, string.Empty);
        }
    }

    private string BuildConversationHistoryBlock(IntentHandlerContext? context, int currentPageId)
    {
        if (context?.ConversationHistory == null || !context.ConversationHistory.Any()) return string.Empty;

        var allSqlTurns = context.ConversationHistory.Where(t => !string.IsNullOrEmpty(t.SqlExecuted)).ToList();
        
        var turnsWithSql = allSqlTurns
            .Where(t => (t.PageId ?? 0) == currentPageId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(5)
            .ToList();
            
        if (allSqlTurns.Count > turnsWithSql.Count)
        {
            _logger.LogWarning("🔪 [UnifiedHandler] HISTORY SLICED: Removed {HiddenTurns} unrelated prior SQL queries from LLM context to prevent cross-page contamination (Current Context PageID: {CurrentPageId}).", 
                allSqlTurns.Count - turnsWithSql.Count, currentPageId);
        }

        if (!turnsWithSql.Any()) return string.Empty;

        var historyBuilder = new System.Text.StringBuilder();
        for (int i = 0; i < turnsWithSql.Count; i++)
        {
            historyBuilder.AppendLine($"[Turn {i + 1}] Q: \"{turnsWithSql[i].Question}\"");
            historyBuilder.AppendLine($"         SQL: {CompressSqlForHistory(turnsWithSql[i].SqlExecuted ?? string.Empty)}");
        }

        return historyBuilder.ToString().Trim();
    }

    private static List<string> ExtractTablesFromTailConditions(ShippedPageContext context)
    {
        var tables = new List<string>();
        var allConditions = new List<string>();
        
        if (!string.IsNullOrEmpty(context.MasterSetup?.TailCondition))
            allConditions.Add(context.MasterSetup.TailCondition);
        foreach (var d in context.DetailSetups ?? new())
            if (!string.IsNullOrEmpty(d.TailCondition))
                allConditions.Add(d.TailCondition);

        TSqlParser parser = _sqlParser;  // reuse static instance (thread-safe)
        var tableVisitor = new ExtractTablesVisitor();

        foreach (var cond in allConditions)
        {
            var cleanCond = cond.Trim();
            if (cleanCond.StartsWith("where", StringComparison.OrdinalIgnoreCase)) 
                cleanCond = cleanCond.Substring(5).Trim();

            var dummyQuery = $"SELECT 1 FROM DUMMY WHERE {cleanCond}";
            using (var reader = new StringReader(dummyQuery))
            {
                var fragment = parser.Parse(reader, out IList<ParseError> errors);
                if (!errors.Any())
                {
                    fragment.Accept(tableVisitor);
                }
            }
        }

        return tableVisitor.Tables.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// يضغط الـ SQL المخزون في الـ Conversation History لتقليل استهلاك الـ Tokens.
    /// يحذف: TOP N، Whitespace الزائد، TailCondition المكررة (يُعيدها الـ Rewriter دائماً).
    /// يحتفظ بـ: SELECT Columns (بالـ Arabic Labels)، JOINs، WHERE conditions الحقيقية.
    /// </summary>
    private static string CompressSqlForHistory(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql;

        // 1. إزالة TOP N (يُضيفها الـ Rewriter تلقائياً)
        var result = Regex.Replace(sql, @"\bSELECT\s+TOP\s+\d+\s+",
            "SELECT ", RegexOptions.IgnoreCase);

        // 2. ضغط المسافات والأسطر الزائدة إلى مسافة واحدة
        result = Regex.Replace(result, @"\s+", " ").Trim();

        // 3. حذف الـ TailCondition المكررة التي يحقنها الـ Rewriter (AND ((1 = 1 AND ...))
        // هذا الجزء يأتي دائماً في نهاية الـ WHERE ويبدأ بـ AND ((1 = 1
        result = Regex.Replace(result,
            @"\s*AND\s*\(\s*\(\s*1\s*=\s*1.*?\)\s*\)\s*;?\s*$",
            ";", RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();

        // 4. تنظيف نهاية السطر
        if (!result.EndsWith(";")) result += ";";

        return result;
    }

    /// <summary>
    /// يضغط ShippedPageContext إلى صيغة Pipe-Delimited مضغوطة وصديقة للـ LLM.
    /// بدلاً من JSON مكرر (~27k chars) → سطور نظيفة (~9k chars).
    ///
    /// Column Format per row:
    ///   SearchName | ColumnName | InsertType | arLabel | enLabel | [Dro:Model:Value:Text]
    /// </summary>
    private static string CompressPageContextForPrompt(ShippedPageContext ctx, List<ColumnSynonym> approvedSynonyms)
    {
        var sb = new System.Text.StringBuilder();

        // ── Page Header ──────────────────────────────────────────────────────
        sb.AppendLine($"PageId={ctx.PageId} | PageName={ctx.PageName}");
        sb.AppendLine();

        // ── Master Setup ─────────────────────────────────────────────────────
        sb.AppendLine($"[MASTER] Table={ctx.MasterSetup.TableName} | PK={ctx.MasterSetup.PrimaryKey} | Alias=TM");
        if (!string.IsNullOrWhiteSpace(ctx.MasterSetup.TailCondition))
            sb.AppendLine($"[MASTER.TailCondition] {ctx.MasterSetup.TailCondition.Trim()}");

        sb.AppendLine("[MASTER.COLUMNS] SearchName | ColumnName | InsertType | arLabel | enLabel | DroInfo");
        foreach (var col in ctx.MasterSetup.Columns)
        {
            var dro = col.DroModel != null
                ? $"Dro:{col.DroModel}:{col.DroValue}:{col.DroText}"
                : string.Empty;

            var colSynonyms = approvedSynonyms
                .Where(s => s.TableName.Equals(ctx.MasterSetup.TableName, StringComparison.OrdinalIgnoreCase)
                         && s.ColumnName.Equals(col.ColumnName, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.UserTerm)
                .ToList();

            var synonymPart = colSynonyms.Any() ? $" (مرادفات: {string.Join("، ", colSynonyms)})" : string.Empty;
            sb.AppendLine($"  {col.SearchName} | {col.ColumnName} | {col.InsertType} | {col.arColumnLabel}{synonymPart} | {col.enColumnLabel} | {dro}");
        }
        sb.AppendLine();

        // ── Detail Setups ─────────────────────────────────────────────────────
        foreach (var detail in ctx.DetailSetups ?? new())
        {
            sb.AppendLine($"[DETAIL] Table={detail.TableName} | FK={detail.ForeignKey} | Alias=TD");
            if (!string.IsNullOrWhiteSpace(detail.TailCondition))
                sb.AppendLine($"[DETAIL.TailCondition] {detail.TailCondition.Trim()}");

            sb.AppendLine("[DETAIL.COLUMNS] SearchName | ColumnName | InsertType | arLabel | enLabel | DroInfo");
            foreach (var col in detail.Columns)
            {
                var dro = col.DroModel != null
                    ? $"Dro:{col.DroModel}:{col.DroValue}:{col.DroText}"
                    : string.Empty;

                var colSynonyms = approvedSynonyms
                    .Where(s => s.TableName.Equals(detail.TableName, StringComparison.OrdinalIgnoreCase)
                             && s.ColumnName.Equals(col.ColumnName, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.UserTerm)
                    .ToList();

                var synonymPart = colSynonyms.Any() ? $" (مرادفات: {string.Join("، ", colSynonyms)})" : string.Empty;
                sb.AppendLine($"  {col.SearchName} | {col.ColumnName} | {col.InsertType} | {col.arColumnLabel}{synonymPart} | {col.enColumnLabel} | {dro}");
            }
            sb.AppendLine();
        }

        // ── Business Rules ─────────────────────────────────────────────────────
        if (ctx.BusinessRules?.Any() == true)
        {
            sb.AppendLine("[BUSINESS_RULES]");
            foreach (var rule in ctx.BusinessRules)
                sb.AppendLine($"  • {rule}");
        }

        return sb.ToString().Trim();
    }

    private static string? ResolveAliasToTable(string mappedToColumn, ShippedPageContext? ctx)
    {
        if (ctx == null || !mappedToColumn.Contains('.')) return null;
        var alias = mappedToColumn.Split('.')[0].Trim().ToUpperInvariant();
        return alias switch
        {
            "TM" => ctx.MasterSetup?.TableName,
            "TD" => ctx.DetailSetups?.FirstOrDefault()?.TableName,
            _ => null
        };
    }

    private static bool ContainsArabic(string text)
        => text.Any(c => c >= '\u0600' && c <= '\u06FF');

    private class ExtractTablesVisitor : TSqlFragmentVisitor
    {
        public List<string> Tables { get; } = new List<string>();

        public override void Visit(NamedTableReference node)
        {
            if (node.SchemaObject?.BaseIdentifier?.Value != null)
            {
                var tableName = node.SchemaObject.BaseIdentifier.Value;
                if (!tableName.Equals("DUMMY", StringComparison.OrdinalIgnoreCase))
                {
                    Tables.Add(tableName);
                }
            }
            base.Visit(node);
        }
    }
}