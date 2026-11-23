using DynamicAi.Application.Exceptions;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicAi.Application.Services;

/// <summary>
/// Orchestrates directed page query execution: isolated history, semantic cache lookup,
/// unified LLM handler execution, optional prediction pass, and background observability logging.
/// </summary>
public class QueryOrchestrator : IQueryOrchestrator
{
    private readonly IUnifiedQueryHandler _unifiedHandler;
    private readonly IPredictionService _predictionService;
    private readonly IQueryCacheService _queryCacheService;
    private readonly IConversationContextService _contextService;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
    private readonly ILogger<QueryOrchestrator> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly bool _queryCacheEnabled;

    public QueryOrchestrator(
        IUnifiedQueryHandler unifiedHandler,
        IPredictionService predictionService,
        IQueryCacheService queryCacheService,
        IConversationContextService contextService,
        ILlmUsageTracker usageTracker,
        IEmbeddingGenerator<string, Embedding<float>> embeddingService,
        ILogger<QueryOrchestrator> logger,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory)
    {
        _unifiedHandler = unifiedHandler;
        _predictionService = predictionService;
        _queryCacheService = queryCacheService;
        _contextService = contextService;
        _usageTracker = usageTracker;
        _embeddingService = embeddingService;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _queryCacheEnabled = !bool.TryParse(configuration["Caching:QueryCache:Enabled"], out var enabled) || enabled;
    }

    public async Task<QueryExecutionResult> HandleDirectedPageQueryAsync(
        DirectedPageAskRequest request,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[Orchestrator] Directed Question: \"{Question}\" | Page: {PageName} (ID: {PageId})",
            request.Question, request.ExplicitContext.PageName, request.PageId);

        var startTime = DateTime.UtcNow;
        var queryLogId = Guid.NewGuid();
        var sessionId = request.SessionId ?? "direct-integration";

        var recentTurns = await _contextService.GetRecentContextAsync(sessionId, 3, request.PageId, ct);

        var sharedEmbedding = await _embeddingService.GenerateAsync(request.Question, cancellationToken: ct);
        float[] sharedVector = sharedEmbedding.Vector.ToArray();

        CachedQueryResult? cachedResult = null;
        if (_queryCacheEnabled)
        {
            cachedResult = await _queryCacheService.TryGetCachedSqlAsync(
                sharedVector,
                request.TenantId,
                request.ComID ?? 1,
                request.PageId,
                ct);

            if (cachedResult != null)
            {
                _logger.LogInformation("[Orchestrator] ⚡ Directed Cache HIT! CacheId: {CacheId}", cachedResult.CacheId);
            }
        }

        var context = new IntentHandlerContext
        {
            PreviousQuestion = request.PreviousQuestion,
            PreviousSql = request.PreviousSql,
            QueryLogId = queryLogId,
            TenantId = request.TenantId,
            CompanyId = request.ComID,
            SessionId = request.SessionId,
            ConversationHistory = recentTurns,
            PrecomputedEmbedding = sharedVector,
            ExplicitPageId = request.PageId,
            InjectedContext = request.ExplicitContext,
            CachedSql = cachedResult?.Sql,
            CachedPageId = cachedResult?.PageId,
            CachedIntent = cachedResult?.Intent,
            CachedCacheId = cachedResult?.CacheId,
            ConnectionString = request.ConnectionString
        };

        QueryExecutionResult? result = null;

        try
        {
            result = await _unifiedHandler.HandleAsync(request.Question, context, ct);

            if (result.IsSuccess && result.Intent == "PredictionWithDataRetrieval")
            {
                await ExecutePredictionPassAsync(result, request, sessionId, queryLogId, ct);
            }

            if (!result.IsSuccess)
            {
                await HandleFailureResultAsync(result, sessionId, "DirectedIntegration", queryLogId, ct);
            }

            var totalDurationMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;

            if (result.IsSuccess && !result.IsCached)
            {
                PersistSuccessInBackground(request, result, sharedVector, sessionId, queryLogId, totalDurationMs);
            }
            else if (result.IsSuccess && result.IsCached)
            {
                PersistCacheHitInBackground(request, result, queryLogId, totalDurationMs);
            }

            if (string.IsNullOrEmpty(result.Intent) || result.Intent == "Unknown")
            {
                result.Intent = nameof(IntentType.DataRetrieval);
            }

            _logger.LogInformation("[Orchestrator] Directed Pipeline Completed in {Duration}ms", totalDurationMs);
            return result;
        }
        catch (InteractionRequiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var latencyMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.LogError(ex, "[Orchestrator] Pipeline Failed after {Duration}ms", latencyMs);
            PersistErrorInBackground(request, ex.Message, queryLogId, latencyMs, result?.Intent ?? "DirectedIntegration");
            throw;
        }
    }

    private async Task ExecutePredictionPassAsync(
        QueryExecutionResult result,
        DirectedPageAskRequest request,
        string sessionId,
        Guid queryLogId,
        CancellationToken ct)
    {
        await _contextService.SaveTurnAsync(
            sessionId,
            request.Question,
            "DataRetrieval",
            result.RewrittenSql,
            result.Data,
            result.PageId,
            ct);

        var predictionText = await _predictionService.GeneratePredictionAsync(
            request.Question,
            sessionId,
            request.TenantId,
            request.ComID,
            queryLogId,
            ct);

        if (!string.IsNullOrEmpty(predictionText))
        {
            result.QueryInsights ??= new QueryInsightData { RetrievedContext = new List<string>() };
            result.QueryInsights.Reasoning = predictionText;
        }
    }

    private async Task HandleFailureResultAsync(
        QueryExecutionResult result,
        string sessionId,
        string fallbackIntent,
        Guid queryLogId,
        CancellationToken ct)
    {
        _logger.LogWarning("[Orchestrator] Handler returned failure: {Error}", result.ErrorMessage);

        if (result.ErrorMessage?.StartsWith("CLARIFICATION_REQUIRED:") == true)
        {
            var clarification = result.ErrorMessage.Replace("CLARIFICATION_REQUIRED:", "").Trim();
            var intent = !string.IsNullOrEmpty(result.Intent) && result.Intent != "Unknown" ? result.Intent : fallbackIntent;

            await _contextService.SaveTurnAsync(
                sessionId,
                clarification,
                intent,
                result.RewrittenSql,
                null,
                result.PageId,
                ct);

            throw new InteractionRequiredException(clarification, queryLogId, result.ConfidenceScore ?? 0);
        }
    }

    private void PersistSuccessInBackground(
        DirectedPageAskRequest request,
        QueryExecutionResult result,
        float[] sharedVector,
        string sessionId,
        Guid queryLogId,
        int latencyMs)
    {
        var summary = _usageTracker.GetSessionSummary();
        var finalHandlerType = "DirectedIntegration_" + (result.Intent ?? "DataRetrieval");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var queryLogRepo = scope.ServiceProvider.GetRequiredService<IQueryLogRepository>();
                var contextService = scope.ServiceProvider.GetRequiredService<IConversationContextService>();
                var queryCacheService = scope.ServiceProvider.GetRequiredService<IQueryCacheService>();
                var feedbackService = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

                await queryLogRepo.AddAsync(new QueryLog
                {
                    Id = queryLogId,
                    RawQuestion = request.Question,
                    GeneratedSql = result.RewrittenSql,
                    WasSuccessful = true,
                    SchemaContext = result.SchemaContext,
                    TenantId = request.TenantId,
                    CompanyId = request.ComID,
                    PageId = request.PageId,
                    CreatedAt = DateTime.UtcNow,
                    LatencyMs = latencyMs,
                    HandlerType = finalHandlerType,
                    InputTokens = (int)summary.TotalInputTokens,
                    OutputTokens = (int)summary.TotalOutputTokens,
                    GeminiCost = (decimal)summary.TotalCostUsd,
                    ConfidenceScore = result.ConfidenceScore,
                    SelfHealingUsed = result.SelfHealingUsed
                });

                await feedbackService.AutoCaptureExampleAsync(queryLogId, request.ComID);

                if (_queryCacheEnabled && !string.IsNullOrWhiteSpace(result.RewrittenSql))
                {
                    await queryCacheService.StoreCachedSqlAsync(
                        queryLogId,
                        request.Question,
                        sharedVector,
                        result.RewrittenSql,
                        request.PageId,
                        request.TenantId,
                        request.ComID ?? 0,
                        finalHandlerType);
                }

                await contextService.SaveTurnAsync(
                    sessionId,
                    request.Question,
                    finalHandlerType,
                    result.RewrittenSql,
                    result.Data,
                    request.PageId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Orchestrator] Background persistence failed for QueryLog {LogId}", queryLogId);
            }
        });
    }

    private void PersistCacheHitInBackground(
        DirectedPageAskRequest request,
        QueryExecutionResult result,
        Guid queryLogId,
        int latencyMs)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var queryLogRepo = scope.ServiceProvider.GetRequiredService<IQueryLogRepository>();

                await queryLogRepo.AddAsync(new QueryLog
                {
                    Id = queryLogId,
                    RawQuestion = request.Question,
                    GeneratedSql = result.RewrittenSql,
                    WasSuccessful = true,
                    SchemaContext = "Served from Directed Cache",
                    TenantId = request.TenantId,
                    CompanyId = request.ComID,
                    PageId = request.PageId,
                    CreatedAt = DateTime.UtcNow,
                    LatencyMs = latencyMs,
                    HandlerType = "DirectedCacheHit",
                    InputTokens = 0,
                    OutputTokens = 0,
                    GeminiCost = 0m,
                    ConfidenceScore = 1.0f
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Orchestrator] Background Cache-HIT logging failed for QueryLog {LogId}", queryLogId);
            }
        });
    }

    private void PersistErrorInBackground(
        DirectedPageAskRequest request,
        string errorMessage,
        Guid queryLogId,
        int latencyMs,
        string handlerType)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var queryLogRepo = scope.ServiceProvider.GetRequiredService<IQueryLogRepository>();

                await queryLogRepo.AddAsync(new QueryLog
                {
                    Id = queryLogId,
                    RawQuestion = request.Question,
                    WasSuccessful = false,
                    ErrorMessage = errorMessage,
                    TenantId = request.TenantId,
                    CompanyId = request.ComID,
                    PageId = request.PageId,
                    CreatedAt = DateTime.UtcNow,
                    LatencyMs = latencyMs,
                    HandlerType = handlerType
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Orchestrator] Background error logging failed for QueryLog {LogId}", queryLogId);
            }
        });
    }
}
