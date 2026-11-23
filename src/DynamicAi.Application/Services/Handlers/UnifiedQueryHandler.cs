using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicAi.Application.Exceptions;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Observability;
using DynamicAi.Application.Prompts;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace DynamicAi.Application.Services.Handlers;

/// <summary>
/// Single unified LLM query handler for directed page integration.
/// Orchestrates prompt rendering, LLM invocation, JSON/SQL self-healing, synonym capture, and SQL execution.
/// </summary>
public class UnifiedQueryHandler : IUnifiedQueryHandler
{
    private readonly IPromptTemplateEngine _promptEngine;
    private readonly IChatCompletionService _chatService;
    private readonly ISqlExecutionPipeline _sqlPipeline;
    private readonly IQueryCacheService _queryCacheService;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly IDbSchemaProvider _schemaProvider;
    private readonly IColumnSynonymRepository _synonymRepository;
    private readonly IMemoryCache _memoryCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UnifiedQueryHandler> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly bool _compactDirectedContext;
    private readonly bool _logSensitivePayloads;
    private readonly string _chatModel;
    private readonly LlmProviderPricingConfig? _pricingConfig;

    public UnifiedQueryHandler(
        IPromptTemplateEngine promptEngine,
        IChatCompletionService chatService,
        ISqlExecutionPipeline sqlPipeline,
        IQueryCacheService queryCacheService,
        ILlmUsageTracker usageTracker,
        IDbSchemaProvider schemaProvider,
        IColumnSynonymRepository synonymRepository,
        IMemoryCache memoryCache,
        IServiceScopeFactory scopeFactory,
        ILogger<UnifiedQueryHandler> logger,
        IConfiguration configuration)
    {
        _promptEngine = promptEngine;
        _chatService = chatService;
        _sqlPipeline = sqlPipeline;
        _queryCacheService = queryCacheService;
        _usageTracker = usageTracker;
        _schemaProvider = schemaProvider;
        _synonymRepository = synonymRepository;
        _memoryCache = memoryCache;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        _compactDirectedContext = configuration.GetValue<bool>("Features:DirectedContextCompactEnabled", false);
        _logSensitivePayloads = configuration.GetValue<bool>(DynamicAiTelemetry.LogSensitivePayloadsConfigKey, false);
        _chatModel = configuration["Gemini:ChatModel"] ?? "unknown";
        _pricingConfig = configuration.GetSection("LlmProviderPricing").Get<LlmProviderPricingConfig>();
    }

    public async Task<QueryExecutionResult> HandleAsync(
        string userQuestion,
        IntentHandlerContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = context?.TenantId ?? "default";
        var companyId = context?.CompanyId;
        var finalContext = context?.InjectedContext;
        int currentPageId = finalContext?.PageId ?? context?.ExplicitPageId ?? 0;

        // Phase 0: Cache Bypass
        var cachedResult = await TryExecuteCachedSqlAsync(context, tenantId, companyId, finalContext, currentPageId, cancellationToken);
        if (cachedResult != null) return cachedResult;

        if (finalContext == null)
        {
            throw new InvalidOperationException("InjectedContext (ExplicitContext) is required for directed page query execution.");
        }

        // Phase 1: Parallel Schema Prefetch
        var schemaPrefetchTask = StartSchemaPrefetchIfApplicable(finalContext, context?.ConnectionString, cancellationToken);

        // Phase 2: Load Synonyms & Render Master Prompt
        string renderedPrompt;
        using (var schemaActivity = DynamicAiTelemetry.ActivitySource.StartActivity("schema.preprocess"))
        {
            schemaActivity?.SetTag("page.id", currentPageId);
            var approvedSynonyms = await GetApprovedSynonymsAsync(currentPageId, cancellationToken);
            int schemaChars;
            (renderedPrompt, schemaChars) = await RenderMasterPromptAsync(
                userQuestion, finalContext, context, companyId, currentPageId, approvedSynonyms);
            schemaActivity?.SetTag("schema.chars", schemaChars);
        }

        // Phase 3 & 4: Call LLM, Track Usage & Parse Unified JSON (with JSON self-healing retry)
        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(renderedPrompt);
        chatHistory.AddUserMessage(userQuestion);

        if (_logSensitivePayloads && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[UnifiedHandler] FULL RENDERED PROMPT ({Chars} chars): {Prompt}",
                renderedPrompt.Length, renderedPrompt);
        }

        DynamicAiTelemetry.PromptChars.Record(
            renderedPrompt.Length,
            new KeyValuePair<string, object?>("llm.model", _chatModel));

        bool selfHealingUsed = false;
        string rawResponse;
        UnifiedLlmResponse? parsedResponse;

        using (var llmActivity = DynamicAiTelemetry.ActivitySource.StartActivity("llm.generate"))
        {
            var initialCall = await InvokeLlmAndTrackAsync(
                chatHistory,
                promptCharCount: renderedPrompt.Length + userQuestion.Length,
                context,
                tenantId,
                companyId,
                selfHealing: false,
                cancellationToken);

            rawResponse = initialCall.RawResponse;
            int totalInputTokens = initialCall.InputTokens;
            int totalOutputTokens = initialCall.OutputTokens;
            string modelName = initialCall.ModelName;

            parsedResponse = ExtractAndParseJson(rawResponse);
            if (parsedResponse == null)
            {
                selfHealingUsed = true;
                var retryCall = await RetryJsonFixAsync(chatHistory, rawResponse, context, tenantId, companyId, cancellationToken);
                parsedResponse = retryCall.Parsed;
                rawResponse = retryCall.RawResponse;
                totalInputTokens += retryCall.InputTokens;
                totalOutputTokens += retryCall.OutputTokens;
            }

            llmActivity?.SetTag("llm.model", modelName);
            llmActivity?.SetTag("llm.tokens.in", totalInputTokens);
            llmActivity?.SetTag("llm.tokens.out", totalOutputTokens);
            llmActivity?.SetTag("llm.intent", parsedResponse?.Intent ?? "Unknown");
            llmActivity?.SetTag("llm.self_healing", selfHealingUsed);

            if (parsedResponse == null)
            {
                llmActivity?.SetStatus(ActivityStatusCode.Error, "Failed to parse JSON from LLM");
                return new QueryExecutionResult
                {
                    Data = new[] { new { Message = "عذراً، لم أتمكن من صياغة الإجابة بالشكل الصحيح.", Type = "Error" } },
                    QueryLogId = context?.QueryLogId,
                    ErrorMessage = "Failed to parse JSON from LLM",
                    SelfHealingUsed = selfHealingUsed
                };
            }
        }

        if (parsedResponse.NeedsClarification && !string.IsNullOrWhiteSpace(parsedResponse.ClarificationQuestion))
        {
            return new QueryExecutionResult
            {
                ErrorMessage = $"CLARIFICATION_REQUIRED: {parsedResponse.ClarificationQuestion.Trim()}",
                QueryLogId = context?.QueryLogId,
                ConfidenceScore = (float?)parsedResponse.Confidence,
                Intent = parsedResponse.Intent,
                PageId = currentPageId,
                SelfHealingUsed = selfHealingUsed
            };
        }

        CaptureSchemaLinkingSynonyms(parsedResponse, finalContext, currentPageId);

        // Phase 5: Execute Route
        if (parsedResponse.IsSqlIntent)
        {
            return await ExecuteSqlWithSelfHealingAsync(
                parsedResponse, rawResponse, chatHistory,
                tenantId, companyId, finalContext, currentPageId,
                context, selfHealingUsed, schemaPrefetchTask, cancellationToken);
        }

        if (parsedResponse.IsPredictionIntent || parsedResponse.IsChatIntent)
        {
            var resultType = parsedResponse.IsPredictionIntent ? "Prediction" : "Chat";
            return new QueryExecutionResult
            {
                Data = new[] { new { Message = parsedResponse.Content.Message, Type = resultType, Explanation = parsedResponse.Content.Explanation } },
                QueryLogId = context?.QueryLogId,
                ConfidenceScore = (float?)parsedResponse.Confidence,
                IsCached = false,
                QueryInsights = new QueryInsightData { Reasoning = parsedResponse.Content.Explanation },
                Intent = parsedResponse.Intent,
                SelfHealingUsed = selfHealingUsed
            };
        }

        return new QueryExecutionResult
        {
            Data = new[] { new { Message = "عذراً، لم أتمكن من معالجة طلبك.", Type = "Unknown" } },
            QueryLogId = context?.QueryLogId,
            SelfHealingUsed = selfHealingUsed
        };
    }

    private async Task<QueryExecutionResult?> TryExecuteCachedSqlAsync(
        IntentHandlerContext? context,
        string tenantId,
        int? companyId,
        ShippedPageContext? finalContext,
        int currentPageId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context?.CachedSql)) return null;

        _logger.LogInformation("[UnifiedHandler] ⚡ GLOBAL CACHE HIT (ID: {CacheId}). Bypassing LLM completely!", context.CachedCacheId);
        try
        {
            var cachedSqlResult = await _sqlPipeline.ExecuteAsync(
                context.CachedSql,
                tenantId,
                companyId,
                finalContext,
                context.CachedPageId ?? currentPageId,
                context,
                ct,
                schemaPrefetchTask: null);

            cachedSqlResult.IsCached = true;
            cachedSqlResult.CacheId = context.CachedCacheId;
            cachedSqlResult.Intent = !string.IsNullOrEmpty(context.CachedIntent) ? context.CachedIntent : nameof(IntentType.DataRetrieval);
            cachedSqlResult.QueryInsights ??= new QueryInsightData();
            cachedSqlResult.QueryInsights.RetrievedContext ??= new List<string>();
            cachedSqlResult.QueryInsights.RetrievedContext.Add("⚡ Served directly from Global Query Cache");
            return cachedSqlResult;
        }
        catch (Exception cacheEx)
        {
            _logger.LogWarning(cacheEx, "[UnifiedHandler] ⚠️ CACHE FALLBACK: Cached SQL failed ({Error}). Invalidating cache.", cacheEx.Message);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (context.PrecomputedEmbedding != null)
                    {
                        await _queryCacheService.DeleteSimilarCacheAsync(context.PrecomputedEmbedding, tenantId, companyId ?? 0, currentPageId > 0 ? currentPageId : null);
                    }
                }
                catch { /* Non-blocking cache cleanup */ }
            });
            return null;
        }
    }

    private Task<Dictionary<string, HashSet<string>>>? StartSchemaPrefetchIfApplicable(
        ShippedPageContext finalContext,
        string? connectionString,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(connectionString)) return null;

        var viewNames = new List<string?> { finalContext.MasterSetup?.TableName }
            .Concat(finalContext.DetailSetups?.Select(d => d.TableName) ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();

        var tailConditionTables = PageContextFormatter.ExtractTablesFromTailConditions(finalContext);
        var addedFromTail = tailConditionTables.Where(t => !viewNames.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        viewNames.AddRange(addedFromTail);

        if (!viewNames.Any()) return null;

        _logger.LogInformation("[UnifiedHandler] 🔄 PARALLEL: Starting schema prefetch for {Count} views.", viewNames.Count);
        return _schemaProvider.GetSchemaForViewsAsync(connectionString, viewNames, ct);
    }

    private async Task<List<ColumnSynonym>> GetApprovedSynonymsAsync(int currentPageId, CancellationToken ct)
    {
        if (currentPageId <= 0) return new List<ColumnSynonym>();

        var cacheKey = $"synonyms_page_{currentPageId}";
        if (_memoryCache.TryGetValue(cacheKey, out List<ColumnSynonym>? cached) && cached != null)
        {
            return cached;
        }

        var synonyms = await _synonymRepository.GetApprovedByPageAsync(currentPageId, ct);
        _memoryCache.Set(cacheKey, synonyms, TimeSpan.FromMinutes(120));
        return synonyms;
    }

    private async Task<(string Prompt, int SchemaChars)> RenderMasterPromptAsync(
        string userQuestion,
        ShippedPageContext finalContext,
        IntentHandlerContext? context,
        int? companyId,
        int currentPageId,
        List<ColumnSynonym> approvedSynonyms)
    {
        string schemaContent = _compactDirectedContext
            ? PageContextFormatter.CompressPageContextForPrompt(finalContext, approvedSynonyms)
            : JsonSerializer.Serialize(finalContext, _jsonOptions);

        string rulesContent = finalContext.BusinessRules?.Any() == true
            ? string.Join("\n", finalContext.BusinessRules.Select((r, i) => $"[Rule {i + 1}]\n{r}"))
            : string.Empty;

        var variables = new Dictionary<string, object>
        {
            { "user_question", userQuestion },
            { "company_id", companyId?.ToString() ?? "0" },
            { "current_date", DateTime.Now.ToString("yyyy-MM-dd") },
            { "current_year", DateTime.Now.Year.ToString() }
        };

        var (_, tailBlock) = TailConditionBuilder.BuildTailConditionBlock(finalContext, isDirectedContext: context?.InjectedContext != null, _logger);
        if (!string.IsNullOrEmpty(tailBlock)) variables["tail_condition"] = tailBlock;
        if (!string.IsNullOrEmpty(schemaContent)) variables["page_attributes_details_listsetups"] = schemaContent;
        if (!string.IsNullOrEmpty(rulesContent)) variables["business_rules"] = rulesContent;

        var historyBlock = ConversationHistoryFormatter.BuildConversationHistoryBlock(context?.ConversationHistory, currentPageId, _logger);
        if (!string.IsNullOrEmpty(historyBlock)) variables["conversation_history"] = historyBlock;

        var prompt = await _promptEngine.RenderAsync("unified_master", variables);
        return (prompt, schemaContent.Length);
    }

    private async Task<(string RawResponse, int InputTokens, int OutputTokens, string ModelName)> InvokeLlmAndTrackAsync(
        ChatHistory chatHistory,
        int promptCharCount,
        IntentHandlerContext? context,
        string tenantId,
        int? companyId,
        bool selfHealing,
        CancellationToken ct)
    {
        var llmStart = Stopwatch.GetTimestamp();
        var response = await _chatService.GetChatMessageContentAsync(chatHistory, cancellationToken: ct);
        var durationMs = Stopwatch.GetElapsedTime(llmStart).TotalMilliseconds;
        var rawResponse = response.Content ?? string.Empty;
        var modelName = !string.IsNullOrWhiteSpace(response.ModelId) ? response.ModelId : _chatModel;

        _logger.LogInformation("[UnifiedHandler] LLM responded in {Duration:F0}ms (SelfHealing={SelfHealing})",
            durationMs, selfHealing);

        if (_logSensitivePayloads && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[UnifiedHandler] RAW LLM RESPONSE ({Chars} chars): {Response}",
                rawResponse.Length, rawResponse);
        }

        int inputTokens = Math.Max(0, promptCharCount / 4);
        int outputTokens = Math.Max(0, rawResponse.Length / 4);

        var costBefore = _usageTracker.GetSessionSummary().TotalCostUsd;
        await _usageTracker.TrackLlmCallAsync(
            LlmOperationType.SqlGeneration,
            requestId: context?.QueryLogId?.ToString() ?? Guid.NewGuid().ToString(),
            inputTokens: inputTokens,
            outputTokens: outputTokens,
            cachedTokens: 0,
            tenantId: tenantId,
            companyId: companyId,
            cancellationToken: ct);

        var pricing = _pricingConfig?.GetActiveModelPricing(modelName);
        double callCostUsd = pricing != null
            ? (double)pricing.CalculateCost(inputTokens, outputTokens, 0)
            : Math.Max(0d, _usageTracker.GetSessionSummary().TotalCostUsd - costBefore);

        DynamicAiTelemetry.LlmDurationMs.Record(durationMs,
            new KeyValuePair<string, object?>("llm.model", modelName),
            new KeyValuePair<string, object?>("llm.self_healing", selfHealing));
        DynamicAiTelemetry.LlmTokens.Add(inputTokens,
            new KeyValuePair<string, object?>("llm.model", modelName),
            new KeyValuePair<string, object?>("token.type", "input"));
        DynamicAiTelemetry.LlmTokens.Add(outputTokens,
            new KeyValuePair<string, object?>("llm.model", modelName),
            new KeyValuePair<string, object?>("token.type", "output"));
        DynamicAiTelemetry.LlmCostUsd.Add(callCostUsd,
            new KeyValuePair<string, object?>("llm.model", modelName));

        return (rawResponse, inputTokens, outputTokens, modelName);
    }

    private async Task<(UnifiedLlmResponse? Parsed, string RawResponse, int InputTokens, int OutputTokens)> RetryJsonFixAsync(
        ChatHistory chatHistory,
        string rawResponse,
        IntentHandlerContext? context,
        string tenantId,
        int? companyId,
        CancellationToken ct)
    {
        _logger.LogWarning("[UnifiedHandler] JSON parse failed. Asking LLM to fix syntax");
        chatHistory.AddAssistantMessage(rawResponse);
        const string fixInstruction =
            "Your previous response contained invalid JSON (likely due to unescaped quotation marks inside string values like 'explanation' or 'sql', or other syntax errors). " +
            "Please FIX the JSON and return EXACTLY the same content but properly escaped. " +
            "Do NOT include any markdown formatting, only raw valid JSON.";
        chatHistory.AddUserMessage(fixInstruction);

        var retryCall = await InvokeLlmAndTrackAsync(
            chatHistory,
            promptCharCount: rawResponse.Length + fixInstruction.Length,
            context,
            tenantId,
            companyId,
            selfHealing: true,
            ct);

        return (ExtractAndParseJson(retryCall.RawResponse), retryCall.RawResponse, retryCall.InputTokens, retryCall.OutputTokens);
    }

    private void CaptureSchemaLinkingSynonyms(UnifiedLlmResponse parsedResponse, ShippedPageContext finalContext, int currentPageId)
    {
        if (parsedResponse.SchemaLinking == null || !parsedResponse.SchemaLinking.Any() || currentPageId <= 0)
            return;

        var entries = parsedResponse.SchemaLinking;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var synonymRepo = scope.ServiceProvider.GetRequiredService<IColumnSynonymRepository>();

                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.UserTerm) || string.IsNullOrWhiteSpace(entry.MappedToColumn))
                        continue;

                    var parts = entry.MappedToColumn.Split('.');
                    var columnName = parts.Length > 1 ? parts[1] : parts[0];
                    var tableName = PageContextFormatter.ResolveAliasToTable(entry.MappedToColumn, finalContext);
                    if (string.IsNullOrEmpty(tableName)) continue;

                    await synonymRepo.UpsertAsync(new ColumnSynonym
                    {
                        PageId = currentPageId,
                        TableName = tableName,
                        ColumnName = columnName,
                        UserTerm = entry.UserTerm.Trim(),
                        Language = entry.UserTerm.Any(c => c >= '\u0600' && c <= '\u06FF') ? "ar" : "en"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[UnifiedHandler] Failed to auto-capture synonym in background.");
            }
        });
    }

    private async Task<QueryExecutionResult> ExecuteSqlWithSelfHealingAsync(
        UnifiedLlmResponse parsedResponse,
        string rawResponse,
        ChatHistory chatHistory,
        string tenantId,
        int? companyId,
        ShippedPageContext finalContext,
        int currentPageId,
        IntentHandlerContext? context,
        bool selfHealingUsed,
        Task<Dictionary<string, HashSet<string>>>? schemaPrefetchTask,
        CancellationToken ct)
    {
        const int maxRetries = 2;
        string currentSql = parsedResponse.Content.Sql;
        string? lastError = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    selfHealingUsed = true;
                    _logger.LogWarning("[UnifiedHandler] Self-healing attempt {Attempt}/{Max}: {Error}", attempt, maxRetries, lastError);

                    chatHistory.AddAssistantMessage(rawResponse);
                    var healPrompt =
                        $"The SQL query you generated failed with the following error:\n" +
                        $"{lastError}\n\n" +
                        $"Please FIX the SQL query. Return your response in the exact same JSON format as before. " +
                        $"Do NOT use columns or tables that don't exist in the schema.";
                    chatHistory.AddUserMessage(healPrompt);

                    using var retryLlmActivity = DynamicAiTelemetry.ActivitySource.StartActivity("llm.generate");
                    var retryCall = await InvokeLlmAndTrackAsync(
                        chatHistory,
                        promptCharCount: rawResponse.Length + healPrompt.Length,
                        context,
                        tenantId,
                        companyId,
                        selfHealing: true,
                        ct);

                    rawResponse = retryCall.RawResponse;
                    var retryParsed = ExtractAndParseJson(rawResponse);

                    retryLlmActivity?.SetTag("llm.model", retryCall.ModelName);
                    retryLlmActivity?.SetTag("llm.tokens.in", retryCall.InputTokens);
                    retryLlmActivity?.SetTag("llm.tokens.out", retryCall.OutputTokens);
                    retryLlmActivity?.SetTag("llm.intent", retryParsed?.Intent ?? "Unknown");
                    retryLlmActivity?.SetTag("llm.self_healing", true);

                    if (retryParsed == null || !retryParsed.IsSqlIntent || string.IsNullOrWhiteSpace(retryParsed.Content?.Sql))
                    {
                        _logger.LogWarning("[UnifiedHandler] LLM retry did not produce valid SQL. Giving up.");
                        break;
                    }
                    currentSql = retryParsed.Content.Sql;
                    parsedResponse = retryParsed;
                }

                var sqlResult = await _sqlPipeline.ExecuteAsync(
                    currentSql,
                    tenantId,
                    companyId,
                    finalContext,
                    currentPageId,
                    context,
                    ct,
                    schemaPrefetchTask);

                sqlResult.ConfidenceScore = (float?)parsedResponse.Confidence;
                sqlResult.QueryInsights ??= new QueryInsightData { RetrievedContext = new List<string>() };
                sqlResult.QueryInsights.RetrievedContext?.Add($"🤖 Intent Detected: {parsedResponse.Intent}");
                sqlResult.QueryInsights.Reasoning = parsedResponse.Content.Explanation;
                sqlResult.Intent = parsedResponse.Intent;
                sqlResult.SelfHealingUsed = selfHealingUsed;

                if (attempt > 0)
                {
                    sqlResult.QueryInsights.RetrievedContext?.Add($"🔄 Self-healed after {attempt} retry(ies)");
                }

                return sqlResult;
            }
            catch (SqlSanitizationException sanitizationEx)
            {
                lastError = sanitizationEx.Message;
                _logger.LogWarning("[UnifiedHandler] 🛡️ Sanitizer rejected SQL: {Error}", lastError);
                if (attempt >= maxRetries)
                {
                    return new QueryExecutionResult
                    {
                        ErrorMessage = "CLARIFICATION_REQUIRED: عذراً، الاستعلام الذي تم توليده يحتوي على أعمدة أو جداول غير موجودة. حاول إعادة صياغة سؤالك.",
                        QueryLogId = context?.QueryLogId,
                        Intent = parsedResponse.Intent,
                        SelfHealingUsed = selfHealingUsed
                    };
                }
            }
            catch (DbException dbEx)
            {
                lastError = $"Database error: {dbEx.Message}";
                _logger.LogWarning("[UnifiedHandler] 💾 Database rejected SQL: {Error}", dbEx.Message);
                if (attempt >= maxRetries)
                {
                    throw;
                }
            }
        }

        return new QueryExecutionResult
        {
            ErrorMessage = lastError ?? "Failed to execute SQL query.",
            QueryLogId = context?.QueryLogId,
            Intent = parsedResponse.Intent,
            SelfHealingUsed = selfHealingUsed
        };
    }

    private UnifiedLlmResponse? ExtractAndParseJson(string llmResponse)
    {
        try
        {
            var jsonMatch = Regex.Match(llmResponse, @"```json\s*(.*?)\s*```", RegexOptions.Singleline);
            var jsonString = jsonMatch.Success ? jsonMatch.Groups[1].Value.Trim() : llmResponse.Trim();

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
}
