using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicAi.Application.Exceptions;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
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
        var approvedSynonyms = await GetApprovedSynonymsAsync(currentPageId, cancellationToken);
        var renderedPrompt = await RenderMasterPromptAsync(userQuestion, finalContext, context, companyId, currentPageId, approvedSynonyms);

        // Phase 3: Call LLM & Track Usage
        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(renderedPrompt);
        chatHistory.AddUserMessage(userQuestion);

        var llmStart = DateTime.UtcNow;
        var response = await _chatService.GetChatMessageContentAsync(chatHistory, cancellationToken: cancellationToken);
        var rawResponse = response.Content ?? string.Empty;
        _logger.LogInformation("[UnifiedHandler] ✓ LLM Responded in {Duration:F0}ms", (DateTime.UtcNow - llmStart).TotalMilliseconds);

        await TrackUsageAsync(renderedPrompt, userQuestion, rawResponse, context, tenantId, companyId, cancellationToken);

        // Phase 4: Parse Unified JSON (with JSON self-healing retry)
        bool selfHealingUsed = false;
        var parsedResponse = ExtractAndParseJson(rawResponse);
        if (parsedResponse == null)
        {
            selfHealingUsed = true;
            (parsedResponse, rawResponse) = await RetryJsonFixAsync(chatHistory, rawResponse, cancellationToken);
            if (parsedResponse == null)
            {
                return new QueryExecutionResult
                {
                    Data = new[] { new { Message = "عذراً، لم أتمكن من صياغة الإجابة بالشكل الصحيح.", Type = "Error" } },
                    QueryLogId = context?.QueryLogId,
                    ErrorMessage = "Failed to parse JSON from LLM",
                    SelfHealingUsed = selfHealingUsed
                };
            }
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

    private async Task<string> RenderMasterPromptAsync(
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

        return await _promptEngine.RenderAsync("unified_master", variables);
    }

    private async Task TrackUsageAsync(
        string renderedPrompt,
        string userQuestion,
        string rawResponse,
        IntentHandlerContext? context,
        string tenantId,
        int? companyId,
        CancellationToken ct)
    {
        await _usageTracker.TrackLlmCallAsync(
            LlmOperationType.SqlGeneration,
            requestId: context?.QueryLogId?.ToString() ?? Guid.NewGuid().ToString(),
            inputTokens: (renderedPrompt.Length + userQuestion.Length) / 4,
            outputTokens: rawResponse.Length / 4,
            cachedTokens: 0,
            tenantId: tenantId,
            companyId: companyId,
            cancellationToken: ct);
    }

    private async Task<(UnifiedLlmResponse? Parsed, string RawResponse)> RetryJsonFixAsync(
        ChatHistory chatHistory,
        string rawResponse,
        CancellationToken ct)
    {
        _logger.LogWarning("[UnifiedHandler] 🔄 JSON Parse failed. Asking LLM to fix syntax...");
        chatHistory.AddAssistantMessage(rawResponse);
        chatHistory.AddUserMessage(
            "Your previous response contained invalid JSON (likely due to unescaped quotation marks inside string values like 'explanation' or 'sql', or other syntax errors). " +
            "Please FIX the JSON and return EXACTLY the same content but properly escaped. " +
            "Do NOT include any markdown formatting, only raw valid JSON.");

        var retryResponse = await _chatService.GetChatMessageContentAsync(chatHistory, cancellationToken: ct);
        var retryRaw = retryResponse.Content ?? string.Empty;
        return (ExtractAndParseJson(retryRaw), retryRaw);
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
                    _logger.LogWarning("[UnifiedHandler] 🔄 Self-Healing Attempt {Attempt}/{Max}: {Error}", attempt, maxRetries, lastError);

                    chatHistory.AddAssistantMessage(rawResponse);
                    chatHistory.AddUserMessage(
                        $"The SQL query you generated failed with the following error:\n" +
                        $"{lastError}\n\n" +
                        $"Please FIX the SQL query. Return your response in the exact same JSON format as before. " +
                        $"Do NOT use columns or tables that don't exist in the schema.");

                    var retryResponse = await _chatService.GetChatMessageContentAsync(chatHistory, cancellationToken: ct);
                    rawResponse = retryResponse.Content ?? string.Empty;

                    var retryParsed = ExtractAndParseJson(rawResponse);
                    if (retryParsed == null || !retryParsed.IsSqlIntent || string.IsNullOrWhiteSpace(retryParsed.Content?.Sql))
                    {
                        _logger.LogWarning("[UnifiedHandler] ❌ LLM retry did not produce valid SQL. Giving up.");
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
