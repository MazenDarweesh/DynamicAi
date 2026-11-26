using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicAi.Application.Exceptions;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Observability;
using DynamicAi.Application.Prompts;
using DynamicAi.Application.Services.SqlEngine;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicAi.Application.Services.Handlers;

/// <summary>
/// Single unified LLM query handler for directed page integration.
/// Orchestrates prompt rendering, optional deterministic Query IR compilation/shadowing,
/// LLM invocation, SQL self-healing, synonym capture, and SQL execution.
/// </summary>
public class UnifiedQueryHandler : IUnifiedQueryHandler
{
    private readonly IPromptTemplateEngine _promptEngine;
    private readonly ILlmClient _llmClient;
    private readonly ISqlExecutionPipeline _sqlPipeline;
    private readonly IQueryCacheService _queryCacheService;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly IDbSchemaProvider _schemaProvider;
    private readonly IColumnSynonymRepository _synonymRepository;
    private readonly IMemoryCache _memoryCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UnifiedQueryHandler> _logger;
    private readonly IQueryIrCompiler _irCompiler;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly bool _compactDirectedContext;
    private readonly bool _logSensitivePayloads;
    private readonly bool _useQueryIrCompiler;
    private readonly bool _queryIrShadowMode;
    private readonly string? _chatModel;

    public UnifiedQueryHandler(
        IPromptTemplateEngine promptEngine,
        ILlmClient llmClient,
        ISqlExecutionPipeline sqlPipeline,
        IQueryCacheService queryCacheService,
        ILlmUsageTracker usageTracker,
        IDbSchemaProvider schemaProvider,
        IColumnSynonymRepository synonymRepository,
        IMemoryCache memoryCache,
        IServiceScopeFactory scopeFactory,
        ILogger<UnifiedQueryHandler> logger,
        IConfiguration configuration,
        IQueryIrCompiler? irCompiler = null)
    {
        _promptEngine = promptEngine;
        _llmClient = llmClient;
        _sqlPipeline = sqlPipeline;
        _queryCacheService = queryCacheService;
        _usageTracker = usageTracker;
        _schemaProvider = schemaProvider;
        _synonymRepository = synonymRepository;
        _memoryCache = memoryCache;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _irCompiler = irCompiler ?? new QueryIrCompiler();
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        _compactDirectedContext = configuration.GetValue<bool>("Features:DirectedContextCompactEnabled", false);
        _logSensitivePayloads = configuration.GetValue<bool>(DynamicAiTelemetry.LogSensitivePayloadsConfigKey, false);
        _useQueryIrCompiler = configuration.GetValue<bool>("Features:UseQueryIrCompiler", false);
        _queryIrShadowMode = configuration.GetValue<bool>("Features:QueryIrShadowMode", false);
        var configuredModel = configuration["Gemini:ChatModel"];
        _chatModel = string.IsNullOrWhiteSpace(configuredModel) ? null : configuredModel;
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

        if (ReadOnlyIntentGuard.TryRejectMutationRequest(userQuestion, out var refusalMessage))
        {
            _logger.LogInformation("[UnifiedHandler] Read-only guard rejected mutation request (PageId={PageId}).", currentPageId);
            return new QueryExecutionResult
            {
                Intent = "Chat",
                Data = new[] { new { Message = refusalMessage, IsReadOnly = true } },
                ConfidenceScore = 1.0f,
                QueryLogId = context?.QueryLogId,
                PageId = currentPageId
            };
        }

        // Phase 0: Cache Bypass
        var cachedResult = await TryExecuteCachedSqlAsync(userQuestion, context, tenantId, companyId, finalContext, currentPageId, cancellationToken);
        if (cachedResult != null) return cachedResult;

        if (finalContext == null)
        {
            throw new InvalidOperationException("InjectedContext (ExplicitContext) is required for directed page query execution.");
        }

        // Phase 1: Parallel Schema Prefetch
        var schemaPrefetchTask = StartSchemaPrefetchIfApplicable(finalContext, context?.ConnectionString, cancellationToken);

        // Phase 2: Load Synonyms
        List<ColumnSynonym> approvedSynonyms;
        using (var schemaActivity = DynamicAiTelemetry.ActivitySource.StartActivity("schema.preprocess"))
        {
            schemaActivity?.SetTag("page.id", currentPageId);
            approvedSynonyms = await GetApprovedSynonymsAsync(currentPageId, cancellationToken);
        }

        int priorInputTokens = 0;
        int priorOutputTokens = 0;
        int priorCachedTokens = 0;
        decimal priorCostUsd = 0m;

        if (_useQueryIrCompiler)
        {
            var irOutcome = await TryExecuteQueryIrAsync(
                userQuestion,
                tenantId,
                companyId,
                finalContext,
                currentPageId,
                context,
                approvedSynonyms,
                schemaPrefetchTask,
                cancellationToken);

            priorInputTokens = irOutcome.InputTokens;
            priorOutputTokens = irOutcome.OutputTokens;
            priorCachedTokens = irOutcome.CachedTokens;
            priorCostUsd = irOutcome.CostUsd;

            if (irOutcome.Result != null)
            {
                return irOutcome.Result;
            }
        }

        var rawSqlResult = await ExecuteRawSqlFlowAsync(
            userQuestion,
            tenantId,
            companyId,
            finalContext,
            currentPageId,
            context,
            approvedSynonyms,
            schemaPrefetchTask,
            priorInputTokens,
            priorOutputTokens,
            priorCachedTokens,
            priorCostUsd,
            cancellationToken);

        if (!_useQueryIrCompiler && _queryIrShadowMode)
        {
            await EvaluateShadowIrAsync(
                userQuestion,
                tenantId,
                companyId,
                finalContext,
                currentPageId,
                context,
                approvedSynonyms,
                schemaPrefetchTask,
                cancellationToken);
        }

        return rawSqlResult;
    }

    private readonly record struct IrExecutionOutcome(
        QueryExecutionResult? Result,
        int InputTokens,
        int OutputTokens,
        int CachedTokens,
        decimal CostUsd);

    private async Task<IrExecutionOutcome> TryExecuteQueryIrAsync(
        string userQuestion,
        string tenantId,
        int? companyId,
        ShippedPageContext finalContext,
        int currentPageId,
        IntentHandlerContext? context,
        List<ColumnSynonym> approvedSynonyms,
        Task<Dictionary<string, HashSet<string>>>? schemaPrefetchTask,
        CancellationToken cancellationToken)
    {
        var actualSchemaMap = await ResolveActualSchemaMapAsync(schemaPrefetchTask, cancellationToken);

        var (irPrompt, schemaChars) = await RenderPromptTemplateAsync(
            "ir_planner",
            finalContext,
            context,
            companyId,
            currentPageId,
            approvedSynonyms,
            actualSchemaMap);

        DynamicAiTelemetry.PromptChars.Record(
            irPrompt.Length,
            new KeyValuePair<string, object?>("llm.model", _chatModel ?? "default"));

        var irRequest = new LlmRequest
        {
            SystemPrompt = irPrompt,
            UserMessage = userQuestion,
            Model = _chatModel,
            Temperature = 0.0,
            MaxOutputTokens = 2048,
            UseQueryIrSchema = true,
            ResponseMimeType = "application/json"
        };

        int inputTokens = 0;
        int outputTokens = 0;
        int cachedTokens = 0;
        decimal costUsd = 0m;
        QueryIrPlan? plan;

        using (var llmActivity = DynamicAiTelemetry.ActivitySource.StartActivity("llm.generate.ir"))
        {
            var irCall = await InvokeLlmAndTrackAsync(
                irRequest,
                context,
                tenantId,
                companyId,
                selfHealing: false,
                cancellationToken);

            inputTokens = irCall.InputTokens;
            outputTokens = irCall.OutputTokens;
            cachedTokens = irCall.CachedTokens;
            costUsd = irCall.CostUsd;

            plan = ExtractAndParseJson<QueryIrPlan>(irCall.RawResponse);

            llmActivity?.SetTag("llm.model", irCall.ModelUsed);
            llmActivity?.SetTag("llm.tokens.in", inputTokens);
            llmActivity?.SetTag("llm.tokens.out", outputTokens);
            llmActivity?.SetTag("llm.tokens.cached", cachedTokens);
            llmActivity?.SetTag("llm.intent", plan?.Intent ?? "Unknown");
            llmActivity?.SetTag("ir.requires_advanced_sql", plan?.RequiresAdvancedSql ?? false);
        }

        if (plan == null)
        {
            _logger.LogWarning("[UnifiedHandler] Failed to parse QueryIrPlan JSON; falling back to RawSql.");
            return new IrExecutionOutcome(null, inputTokens, outputTokens, cachedTokens, costUsd);
        }

        if (plan.NeedsClarification && !string.IsNullOrWhiteSpace(plan.ClarificationQuestion))
        {
            return new IrExecutionOutcome(
                new QueryExecutionResult
                {
                    ErrorMessage = $"CLARIFICATION_REQUIRED: {plan.ClarificationQuestion.Trim()}",
                    QueryLogId = context?.QueryLogId,
                    Intent = plan.Intent,
                    PageId = currentPageId,
                    SelfHealingUsed = false,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    CachedTokens = cachedTokens,
                    LlmCostUsd = costUsd
                },
                inputTokens, outputTokens, cachedTokens, costUsd);
        }

        if (plan.RequiresAdvancedSql)
        {
            _logger.LogInformation("[UnifiedHandler] QueryIrPlan flagged requires_advanced_sql; falling back to RawSql.");
            return new IrExecutionOutcome(null, inputTokens, outputTokens, cachedTokens, costUsd);
        }

        bool isChat = plan.Intent.Equals("Chat", StringComparison.OrdinalIgnoreCase);
        bool isDirectPrediction = plan.Intent.Equals("Prediction", StringComparison.OrdinalIgnoreCase) &&
                                  plan.Select.Count == 0 &&
                                  plan.Aggregations.Count == 0;

        if (isChat || isDirectPrediction)
        {
            var resultType = isChat ? "Chat" : "Prediction";
            return new IrExecutionOutcome(
                new QueryExecutionResult
                {
                    Data = new[] { new { Message = plan.Message ?? string.Empty, Type = resultType, Explanation = plan.Explanation } },
                    QueryLogId = context?.QueryLogId,
                    IsCached = false,
                    QueryInsights = new QueryInsightData { Reasoning = plan.Explanation },
                    Intent = plan.Intent,
                    PageId = currentPageId,
                    SelfHealingUsed = false,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    CachedTokens = cachedTokens,
                    LlmCostUsd = costUsd
                },
                inputTokens, outputTokens, cachedTokens, costUsd);
        }

        CompiledIrSql compiled;
        try
        {
            compiled = _irCompiler.Compile(plan, finalContext, companyId);
        }
        catch (QueryIrCompilationException ex)
        {
            _logger.LogWarning(ex, "[UnifiedHandler] QueryIR compilation failed; falling back to RawSql.");
            return new IrExecutionOutcome(null, inputTokens, outputTokens, cachedTokens, costUsd);
        }

        try
        {
            var sqlResult = await _sqlPipeline.ExecuteAsync(
                compiled.Sql,
                tenantId,
                companyId,
                finalContext,
                currentPageId,
                context,
                cancellationToken,
                schemaPrefetchTask,
                compiled.Parameters);

            sqlResult.QueryInsights ??= new QueryInsightData { RetrievedContext = new List<string>() };
            sqlResult.QueryInsights.RetrievedContext ??= new List<string>();
            sqlResult.QueryInsights.RetrievedContext.Add("⚡ Compiled deterministically via QueryIR");
            sqlResult.QueryInsights.Reasoning = compiled.Explanation;
            sqlResult.Intent = plan.Intent;
            sqlResult.SelfHealingUsed = false;
            sqlResult.InputTokens = inputTokens;
            sqlResult.OutputTokens = outputTokens;
            sqlResult.CachedTokens = cachedTokens;
            sqlResult.LlmCostUsd = costUsd;

            return new IrExecutionOutcome(sqlResult, inputTokens, outputTokens, cachedTokens, costUsd);
        }
        catch (SqlSanitizationException ex)
        {
            _logger.LogWarning(ex, "[UnifiedHandler] QueryIR compiled SQL rejected by sanitizer; falling back to RawSql.");
            return new IrExecutionOutcome(null, inputTokens, outputTokens, cachedTokens, costUsd);
        }
    }

    private async Task<QueryExecutionResult> ExecuteRawSqlFlowAsync(
        string userQuestion,
        string tenantId,
        int? companyId,
        ShippedPageContext finalContext,
        int currentPageId,
        IntentHandlerContext? context,
        List<ColumnSynonym> approvedSynonyms,
        Task<Dictionary<string, HashSet<string>>>? schemaPrefetchTask,
        int priorInputTokens,
        int priorOutputTokens,
        int priorCachedTokens,
        decimal priorCostUsd,
        CancellationToken cancellationToken)
    {
        var actualSchemaMap = await ResolveActualSchemaMapAsync(schemaPrefetchTask, cancellationToken);

        // Render Master Prompt (static/cacheable per schema & context, excluding userQuestion)
        var (renderedPrompt, schemaChars) = await RenderPromptTemplateAsync(
            "unified_master",
            finalContext,
            context,
            companyId,
            currentPageId,
            approvedSynonyms,
            actualSchemaMap);

        Activity.Current?.SetTag("schema.chars", schemaChars);

        // Call LLM, Track Usage & Parse Unified JSON locally
        if (_logSensitivePayloads && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[UnifiedHandler] FULL RENDERED PROMPT ({Chars} chars): {Prompt}",
                renderedPrompt.Length, renderedPrompt);
        }

        DynamicAiTelemetry.PromptChars.Record(
            renderedPrompt.Length,
            new KeyValuePair<string, object?>("llm.model", _chatModel ?? "default"));

        var initialRequest = new LlmRequest
        {
            SystemPrompt = renderedPrompt,
            UserMessage = userQuestion,
            Model = _chatModel,
            Temperature = 0.0,
            MaxOutputTokens = 4096,
            UseUnifiedJsonSchema = true,
            ResponseMimeType = "application/json"
        };

        int totalInputTokens = priorInputTokens;
        int totalOutputTokens = priorOutputTokens;
        int totalCachedTokens = priorCachedTokens;
        decimal totalCostUsd = priorCostUsd;
        UnifiedLlmResponse? parsedResponse;

        using (var llmActivity = DynamicAiTelemetry.ActivitySource.StartActivity("llm.generate"))
        {
            var initialCall = await InvokeLlmAndTrackAsync(
                initialRequest,
                context,
                tenantId,
                companyId,
                selfHealing: false,
                cancellationToken);

            totalInputTokens += initialCall.InputTokens;
            totalOutputTokens += initialCall.OutputTokens;
            totalCachedTokens += initialCall.CachedTokens;
            totalCostUsd += initialCall.CostUsd;

            parsedResponse = ExtractAndParseJson<UnifiedLlmResponse>(initialCall.RawResponse);

            llmActivity?.SetTag("llm.model", initialCall.ModelUsed);
            llmActivity?.SetTag("llm.tokens.in", totalInputTokens);
            llmActivity?.SetTag("llm.tokens.out", totalOutputTokens);
            llmActivity?.SetTag("llm.tokens.cached", totalCachedTokens);
            llmActivity?.SetTag("llm.intent", parsedResponse?.Intent ?? "Unknown");
            llmActivity?.SetTag("llm.self_healing", false);

            if (parsedResponse == null)
            {
                llmActivity?.SetStatus(ActivityStatusCode.Error, "Failed to parse JSON from LLM");
                return new QueryExecutionResult
                {
                    Data = new[] { new { Message = "عذراً، لم أتمكن من صياغة الإجابة بالشكل الصحيح.", Type = "Error" } },
                    QueryLogId = context?.QueryLogId,
                    ErrorMessage = "Failed to parse JSON from LLM",
                    SelfHealingUsed = false,
                    InputTokens = totalInputTokens,
                    OutputTokens = totalOutputTokens,
                    CachedTokens = totalCachedTokens,
                    LlmCostUsd = totalCostUsd
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
                SelfHealingUsed = false,
                InputTokens = totalInputTokens,
                OutputTokens = totalOutputTokens,
                CachedTokens = totalCachedTokens,
                LlmCostUsd = totalCostUsd
            };
        }

        CaptureSchemaLinkingSynonyms(parsedResponse, finalContext, currentPageId, userQuestion);

        // Execute Route
        if (parsedResponse.IsSqlIntent)
        {
            return await ExecuteSqlWithSelfHealingAsync(
                parsedResponse,
                renderedPrompt,
                userQuestion,
                totalInputTokens,
                totalOutputTokens,
                totalCachedTokens,
                totalCostUsd,
                tenantId,
                companyId,
                finalContext,
                currentPageId,
                context,
                schemaPrefetchTask,
                cancellationToken);
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
                SelfHealingUsed = false,
                InputTokens = totalInputTokens,
                OutputTokens = totalOutputTokens,
                CachedTokens = totalCachedTokens,
                LlmCostUsd = totalCostUsd
            };
        }

        return new QueryExecutionResult
        {
            Data = new[] { new { Message = "عذراً، لم أتمكن من معالجة طلبك.", Type = "Unknown" } },
            QueryLogId = context?.QueryLogId,
            SelfHealingUsed = false,
            InputTokens = totalInputTokens,
            OutputTokens = totalOutputTokens,
            CachedTokens = totalCachedTokens,
            LlmCostUsd = totalCostUsd
        };
    }

    private async Task EvaluateShadowIrAsync(
        string userQuestion,
        string tenantId,
        int? companyId,
        ShippedPageContext finalContext,
        int currentPageId,
        IntentHandlerContext? context,
        List<ColumnSynonym> approvedSynonyms,
        Task<Dictionary<string, HashSet<string>>>? schemaPrefetchTask,
        CancellationToken cancellationToken)
    {
        using var shadowActivity = DynamicAiTelemetry.ActivitySource.StartActivity("ir.shadow");
        var sw = Stopwatch.GetTimestamp();
        bool shadowCompiled = false;
        string shadowTables = string.Empty;

        try
        {
            var actualSchemaMap = await ResolveActualSchemaMapAsync(schemaPrefetchTask, cancellationToken);
            var (irPrompt, _) = await RenderPromptTemplateAsync(
                "ir_planner",
                finalContext,
                context,
                companyId,
                currentPageId,
                approvedSynonyms,
                actualSchemaMap);

            var irRequest = new LlmRequest
            {
                SystemPrompt = irPrompt,
                UserMessage = userQuestion,
                Model = _chatModel,
                Temperature = 0.0,
                MaxOutputTokens = 2048,
                UseQueryIrSchema = true,
                ResponseMimeType = "application/json"
            };

            var irCall = await InvokeLlmAndTrackAsync(
                irRequest,
                context,
                tenantId,
                companyId,
                selfHealing: false,
                cancellationToken);

            var plan = ExtractAndParseJson<QueryIrPlan>(irCall.RawResponse);
            if (plan != null && !plan.RequiresAdvancedSql && !plan.NeedsClarification &&
                (plan.Select.Count > 0 || plan.Aggregations.Count > 0))
            {
                var compiled = _irCompiler.Compile(plan, finalContext, companyId);
                shadowCompiled = true;
                shadowTables = string.Join(",", compiled.ReferencedTables);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[UnifiedHandler] Shadow IR evaluation failed without impacting authoritative RawSql.");
        }
        finally
        {
            var shadowLatencyMs = Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
            shadowActivity?.SetTag("shadow.ir.compiled", shadowCompiled);
            shadowActivity?.SetTag("shadow.ir.latency_ms", shadowLatencyMs);
            shadowActivity?.SetTag("shadow.ir.tables", shadowTables);

            _logger.LogInformation(
                "[UnifiedHandler] Shadow IR evaluated: ShadowIrCompiled={ShadowIrCompiled}, ShadowIrLatencyMs={ShadowIrLatencyMs:F1}, ShadowIrTables={ShadowIrTables}",
                shadowCompiled, shadowLatencyMs, shadowTables);
        }
    }

    private async Task<QueryExecutionResult?> TryExecuteCachedSqlAsync(
        string userQuestion,
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
            var cacheIdToDelete = context.CachedCacheId;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(cacheIdToDelete))
                    {
                        await _queryCacheService.DeleteCacheEntryAsync(cacheIdToDelete);
                    }
                    else
                    {
                        await _queryCacheService.DeleteByQuestionAsync(
                            userQuestion,
                            tenantId,
                            companyId ?? 0,
                            currentPageId > 0 ? currentPageId : null);
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "[UnifiedHandler] Failed to invalidate broken cache entry {CacheId}.", cacheIdToDelete);
                }
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
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tailConditionTables = PageContextFormatter.ExtractTablesFromTailConditions(finalContext);
        var addedFromTail = tailConditionTables.Where(t => !viewNames.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        viewNames.AddRange(addedFromTail);

        var dropdownTables = PageContextFormatter.ExtractJoinableDropdownTables(finalContext);
        var addedFromDropdowns = dropdownTables.Where(t => !viewNames.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        viewNames.AddRange(addedFromDropdowns);

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

    private async Task<IReadOnlyDictionary<string, HashSet<string>>?> ResolveActualSchemaMapAsync(
        Task<Dictionary<string, HashSet<string>>>? schemaPrefetchTask,
        CancellationToken cancellationToken)
    {
        if (schemaPrefetchTask == null)
        {
            return null;
        }

        try
        {
            var schemaMap = await schemaPrefetchTask;
            if (schemaMap == null || schemaMap.Count == 0)
            {
                _logger.LogWarning("[UnifiedHandler] Schema prefetch returned null or empty schema map; skipping phantom column filtering (fail-open).");
                return null;
            }

            return schemaMap;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[UnifiedHandler] Schema prefetch failed; skipping phantom column filtering (fail-open).");
            return null;
        }
    }

    private async Task<(string Prompt, int SchemaChars)> RenderPromptTemplateAsync(
        string templateName,
        ShippedPageContext finalContext,
        IntentHandlerContext? context,
        int? companyId,
        int currentPageId,
        List<ColumnSynonym> approvedSynonyms,
        IReadOnlyDictionary<string, HashSet<string>>? actualSchemaMap = null)
    {
        string schemaContent = _compactDirectedContext
            ? PageContextFormatter.CompressPageContextForPrompt(finalContext, approvedSynonyms, actualSchemaMap)
            : JsonSerializer.Serialize(finalContext, _jsonOptions);

        string rulesContent = finalContext.BusinessRules?.Any() == true
            ? string.Join("\n", finalContext.BusinessRules.Select((r, i) => $"[Rule {i + 1}]\n{r}"))
            : string.Empty;

        var variables = new Dictionary<string, object>
        {
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

        var prompt = await _promptEngine.RenderAsync(templateName, variables);
        return (prompt, schemaContent.Length);
    }

    private readonly record struct LlmCallMetrics(
        string RawResponse,
        int InputTokens,
        int OutputTokens,
        int CachedTokens,
        string ModelUsed,
        decimal CostUsd);

    private async Task<LlmCallMetrics> InvokeLlmAndTrackAsync(
        LlmRequest request,
        IntentHandlerContext? context,
        string tenantId,
        int? companyId,
        bool selfHealing,
        CancellationToken ct)
    {
        var llmStart = Stopwatch.GetTimestamp();
        var response = await _llmClient.GenerateAsync(request, ct);
        var durationMs = Stopwatch.GetElapsedTime(llmStart).TotalMilliseconds;
        var rawResponse = response.Text ?? string.Empty;
        var modelUsed = !string.IsNullOrWhiteSpace(response.ModelUsed)
            ? response.ModelUsed
            : (_chatModel ?? "unknown");

        _logger.LogInformation(
            "[UnifiedHandler] LLM responded in {Duration:F0}ms (Model={Model}, In={In}, Out={Out}, Cached={Cached}, SelfHealing={SelfHealing})",
            durationMs, modelUsed, response.InputTokens, response.OutputTokens, response.CachedTokens, selfHealing);

        if (_logSensitivePayloads && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[UnifiedHandler] RAW LLM RESPONSE ({Chars} chars): {Response}",
                rawResponse.Length, rawResponse);
        }

        var costUsd = await _usageTracker.TrackLlmCallAsync(
            LlmOperationType.SqlGeneration,
            requestId: context?.QueryLogId?.ToString() ?? Guid.NewGuid().ToString(),
            inputTokens: response.InputTokens,
            outputTokens: response.OutputTokens,
            cachedTokens: response.CachedTokens,
            tenantId: tenantId,
            companyId: companyId,
            modelName: modelUsed,
            cancellationToken: ct);

        DynamicAiTelemetry.LlmDurationMs.Record(durationMs,
            new KeyValuePair<string, object?>("llm.model", modelUsed),
            new KeyValuePair<string, object?>("llm.self_healing", selfHealing));
        DynamicAiTelemetry.LlmTokens.Add(response.InputTokens,
            new KeyValuePair<string, object?>("llm.model", modelUsed),
            new KeyValuePair<string, object?>("token.type", "input"));
        DynamicAiTelemetry.LlmTokens.Add(response.OutputTokens,
            new KeyValuePair<string, object?>("llm.model", modelUsed),
            new KeyValuePair<string, object?>("token.type", "output"));
        if (response.CachedTokens > 0)
        {
            DynamicAiTelemetry.LlmTokens.Add(response.CachedTokens,
                new KeyValuePair<string, object?>("llm.model", modelUsed),
                new KeyValuePair<string, object?>("token.type", "cached"));
        }
        DynamicAiTelemetry.LlmCostUsd.Add((double)costUsd,
            new KeyValuePair<string, object?>("llm.model", modelUsed));

        return new LlmCallMetrics(
            rawResponse,
            response.InputTokens,
            response.OutputTokens,
            response.CachedTokens,
            modelUsed,
            costUsd);
    }

    private void CaptureSchemaLinkingSynonyms(
        UnifiedLlmResponse parsedResponse,
        ShippedPageContext finalContext,
        int currentPageId,
        string userQuestion)
    {
        if (parsedResponse.SchemaLinking == null ||
            !parsedResponse.SchemaLinking.Any() ||
            currentPageId <= 0 ||
            string.IsNullOrWhiteSpace(userQuestion))
        {
            return;
        }

        var synonymsToPersist = new List<ColumnSynonym>();
        foreach (var entry in parsedResponse.SchemaLinking)
        {
            if (string.IsNullOrWhiteSpace(entry.UserTerm) || string.IsNullOrWhiteSpace(entry.MappedToColumn))
                continue;

            var trimmedTerm = entry.UserTerm.Trim();
            if (!userQuestion.Contains(trimmedTerm, StringComparison.OrdinalIgnoreCase))
                continue;

            var tableName = PageContextFormatter.ResolveAliasToTable(entry.MappedToColumn, finalContext);
            if (string.IsNullOrEmpty(tableName))
                continue;

            var parts = entry.MappedToColumn.Split('.');
            var columnName = (parts.Length > 1 ? parts[1] : parts[0]).Trim();
            if (string.IsNullOrEmpty(columnName))
                continue;

            var matchedColumn = FindColumnDefinition(finalContext, tableName, columnName);
            if (matchedColumn != null && MatchesExistingColumnLabel(trimmedTerm, matchedColumn))
                continue;

            synonymsToPersist.Add(new ColumnSynonym
            {
                PageId = currentPageId,
                TableName = tableName,
                ColumnName = columnName,
                UserTerm = trimmedTerm,
                Language = trimmedTerm.Any(c => c >= '\u0600' && c <= '\u06FF') ? "ar" : "en"
            });
        }

        if (synonymsToPersist.Count == 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var synonymRepo = scope.ServiceProvider.GetRequiredService<IColumnSynonymRepository>();

                foreach (var synonym in synonymsToPersist)
                {
                    await synonymRepo.UpsertAsync(synonym);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[UnifiedHandler] Failed to auto-capture synonym in background.");
            }
        });
    }

    private static ColumnDefinition? FindColumnDefinition(
        ShippedPageContext context,
        string tableName,
        string columnName)
    {
        var normalizedTarget = SqlIdentifierNormalizer.NormalizeTableName(tableName);

        bool MatchesTable(string? setupTable) =>
            !string.IsNullOrWhiteSpace(setupTable) &&
            string.Equals(
                SqlIdentifierNormalizer.NormalizeTableName(setupTable),
                normalizedTarget,
                StringComparison.OrdinalIgnoreCase);

        if (MatchesTable(context.MasterSetup?.TableName))
        {
            var masterMatch = context.MasterSetup?.Columns?.FirstOrDefault(c =>
                string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
            if (masterMatch != null) return masterMatch;
        }

        if (context.DetailSetups != null)
        {
            foreach (var detail in context.DetailSetups)
            {
                if (!MatchesTable(detail?.TableName)) continue;

                var detailMatch = detail?.Columns?.FirstOrDefault(c =>
                    string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
                if (detailMatch != null) return detailMatch;
            }
        }

        return null;
    }

    private static bool MatchesExistingColumnLabel(string term, ColumnDefinition col) =>
        string.Equals(term, col.ColumnName?.Trim(), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(term, col.SearchName?.Trim(), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(term, col.arColumnLabel?.Trim(), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(term, col.enColumnLabel?.Trim(), StringComparison.OrdinalIgnoreCase);

    private async Task<QueryExecutionResult> ExecuteSqlWithSelfHealingAsync(
        UnifiedLlmResponse parsedResponse,
        string renderedPrompt,
        string userQuestion,
        int totalInputTokens,
        int totalOutputTokens,
        int totalCachedTokens,
        decimal totalCostUsd,
        string tenantId,
        int? companyId,
        ShippedPageContext finalContext,
        int currentPageId,
        IntentHandlerContext? context,
        Task<Dictionary<string, HashSet<string>>>? schemaPrefetchTask,
        CancellationToken ct)
    {
        const int maxRetries = 2;
        bool selfHealingUsed = false;
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

                    var healUserMessage =
                        $"Original user question: {userQuestion}\n\n" +
                        $"Your previous SQL query:\n{currentSql}\n\n" +
                        $"Failed with the following error:\n{lastError}\n\n" +
                        $"Please FIX the SQL query. Return your response in the exact same JSON format as before. " +
                        $"Do NOT use columns or tables that don't exist in the schema.";

                    var healRequest = new LlmRequest
                    {
                        SystemPrompt = renderedPrompt,
                        UserMessage = healUserMessage,
                        Model = _chatModel,
                        Temperature = 0.0,
                        MaxOutputTokens = 4096,
                        UseUnifiedJsonSchema = true,
                        ResponseMimeType = "application/json"
                    };

                    using var retryLlmActivity = DynamicAiTelemetry.ActivitySource.StartActivity("llm.generate");
                    var retryCall = await InvokeLlmAndTrackAsync(
                        healRequest,
                        context,
                        tenantId,
                        companyId,
                        selfHealing: true,
                        ct);

                    totalInputTokens += retryCall.InputTokens;
                    totalOutputTokens += retryCall.OutputTokens;
                    totalCachedTokens += retryCall.CachedTokens;
                    totalCostUsd += retryCall.CostUsd;

                    var retryParsed = ExtractAndParseJson<UnifiedLlmResponse>(retryCall.RawResponse);

                    retryLlmActivity?.SetTag("llm.model", retryCall.ModelUsed);
                    retryLlmActivity?.SetTag("llm.tokens.in", retryCall.InputTokens);
                    retryLlmActivity?.SetTag("llm.tokens.out", retryCall.OutputTokens);
                    retryLlmActivity?.SetTag("llm.tokens.cached", retryCall.CachedTokens);
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
                sqlResult.InputTokens = totalInputTokens;
                sqlResult.OutputTokens = totalOutputTokens;
                sqlResult.CachedTokens = totalCachedTokens;
                sqlResult.LlmCostUsd = totalCostUsd;

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
                        SelfHealingUsed = selfHealingUsed,
                        InputTokens = totalInputTokens,
                        OutputTokens = totalOutputTokens,
                        CachedTokens = totalCachedTokens,
                        LlmCostUsd = totalCostUsd
                    };
                }
            }
            catch (DbException dbEx)
            {
                if (IsNonRecoverableDatabaseError(dbEx))
                {
                    _logger.LogWarning(
                        "[UnifiedHandler] 🔌 Non-recoverable database connectivity/auth error detected — skipping LLM self-healing: {Error}",
                        dbEx.Message);
                    throw;
                }

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
            SelfHealingUsed = selfHealingUsed,
            InputTokens = totalInputTokens,
            OutputTokens = totalOutputTokens,
            CachedTokens = totalCachedTokens,
            LlmCostUsd = totalCostUsd
        };
    }

    private static readonly HashSet<int> NonRecoverableSqlErrorNumbers = new()
    {
        -1, 2, 40, 53, 64, 229, 233, 258, 262, 297, 1225,
        4060, 10053, 10054, 10060, 10061, 10928, 10929,
        18456, 40197, 40501, 40613
    };

    private static readonly string[] NonRecoverableErrorMarkers =
    [
        "network-related",
        "refused the network connection",
        "server was not found",
        "transport-level error",
        "connection timeout expired",
        "pre-login handshake",
        "connection has been broken",
        "login failed",
        "cannot open database",
        "permission was denied"
    ];

    /// <summary>
    /// Classifies database connectivity, transport, authentication, and permission errors
    /// that cannot be fixed by rewriting SQL via LLM self-healing.
    /// </summary>
    public static bool IsNonRecoverableDatabaseError(DbException dbEx)
    {
        if (TryGetSqlErrorNumber(dbEx, out var errorNumber) &&
            NonRecoverableSqlErrorNumbers.Contains(errorNumber))
        {
            return true;
        }

        Exception? current = dbEx;
        for (int depth = 0; depth < 6 && current != null; depth++, current = current.InnerException)
        {
            if (current is SocketException or TimeoutException or Win32Exception)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(current.Message))
            {
                foreach (var marker in NonRecoverableErrorMarkers)
                {
                    if (current.Message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryGetSqlErrorNumber(DbException dbEx, out int errorNumber)
    {
        var prop = dbEx.GetType().GetProperty("Number");
        if (prop?.PropertyType == typeof(int) && prop.GetValue(dbEx) is int num)
        {
            errorNumber = num;
            return true;
        }

        errorNumber = 0;
        return false;
    }

    private T? ExtractAndParseJson<T>(string llmResponse) where T : class
    {
        if (string.IsNullOrWhiteSpace(llmResponse))
            return null;

        var jsonMatch = Regex.Match(llmResponse, @"```json\s*(.*?)\s*```", RegexOptions.Singleline);
        var jsonString = jsonMatch.Success ? jsonMatch.Groups[1].Value.Trim() : llmResponse.Trim();

        if (jsonString.StartsWith("```")) jsonString = jsonString.Substring(3);
        if (jsonString.EndsWith("```")) jsonString = jsonString.Substring(0, jsonString.Length - 3);
        jsonString = jsonString.Trim();

        try
        {
            return JsonSerializer.Deserialize<T>(jsonString, _jsonOptions);
        }
        catch (JsonException)
        {
            // Local fallback: extract outermost JSON object in case prose wrapped the payload
            var firstBrace = jsonString.IndexOf('{');
            var lastBrace = jsonString.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                try
                {
                    var sliced = jsonString[firstBrace..(lastBrace + 1)];
                    return JsonSerializer.Deserialize<T>(sliced, _jsonOptions);
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, "[UnifiedHandler] JSON Parse Error (after local brace extraction)");
                    return null;
                }
            }

            _logger.LogError("[UnifiedHandler] JSON Parse Error: no JSON object delimiters found.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UnifiedHandler] JSON Parse Error");
            return null;
        }
    }
}
