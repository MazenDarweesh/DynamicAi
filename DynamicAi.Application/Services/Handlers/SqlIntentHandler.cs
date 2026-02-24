using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace DynamicAi.Application.Services.Handlers;

/// <summary>
/// Handler for DataRetrieval and Analysis intents.
/// Orchestrates the full SQL generation pipeline: Retrieval -> Generation -> Validation -> Rewriting -> Execution.
/// </summary>
public class SqlIntentHandler : IIntentHandler
{
    #region Fields

    private readonly IQueryCondenserService _condenserService;
    private readonly IRagRetrievalService _retrievalService;
    private readonly ISqlGenerationService _generationService;
    private readonly ISqlValidatorService _validatorService;
    private readonly ISqlRewriterService _rewriterService;
    private readonly ISandboxedSqlExecutor _executionService;
    private readonly IQueryCacheService _queryCacheService;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
    private readonly ILlmUsageTracker _usageTracker; // For tracking token usage
    private readonly ILogger<SqlIntentHandler> _logger;
    private readonly bool _queryCacheEnabled;

    #endregion

    #region Constructor

    public SqlIntentHandler(
        IQueryCondenserService condenserService,
        IRagRetrievalService retrievalService,
        ISqlGenerationService generationService,
        ISqlValidatorService validatorService,
        ISqlRewriterService rewriterService,
        ISandboxedSqlExecutor executionService,
        IQueryCacheService queryCacheService,
        IEmbeddingGenerator<string, Embedding<float>> embeddingService,
        ILlmUsageTracker usageTracker,
        ISqlSanitizerService sanitizerService,
        ILogger<SqlIntentHandler> logger,
        IConfiguration configuration)
    {
        _condenserService = condenserService;
        _retrievalService = retrievalService;
        _generationService = generationService;
        _validatorService = validatorService;
        _rewriterService = rewriterService;
        _executionService = executionService;
        _queryCacheService = queryCacheService;
        _embeddingService = embeddingService;
        _usageTracker = usageTracker;
        _logger = logger;
        _logger = logger;
        _queryCacheEnabled = !bool.TryParse(configuration["Caching:QueryCache:Enabled"], out var enabled) || enabled; // Default true
    }

    #endregion

    #region Properties

    public IntentType[] SupportedIntents => new[] { IntentType.DataRetrieval, IntentType.Analysis };

    #endregion

    #region Public Methods

    public bool CanHandle(IntentType intent) => SupportedIntents.Contains(intent);

    public async Task<QueryExecutionResult> HandleAsync(
        string userQuestion,
        IntentClassificationResult intentResult,
        IntentHandlerContext? context = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[SqlHandler] Starting SQL pipeline for intent: {Intent}", intentResult.Intent);

        #region Phase 1: Query Condensing

        _logger.LogInformation("[SqlHandler] Layer 0.5: Condensing Query...");
        var condenserResult = await _condenserService.CondenseQuestionAsync(
            userQuestion,
            context?.PreviousQuestion,
            context?.PreviousSql,
            context?.TenantId,
            context?.CompanyId);

        var condensedQuestion = condenserResult.CondensedQuestion;
        var actionType = condenserResult.ActionType;

        #endregion

        #region Phase 2: Query Cache Check

        float[]? questionEmbedding = null;
        bool isCached = false;

        if (_queryCacheEnabled)
        {
            _logger.LogInformation("[SqlHandler] Layer 0: Checking Query Cache...");
            var embedding = await _embeddingService.GenerateAsync(condensedQuestion);
            questionEmbedding = embedding.Vector.ToArray();

            // Track embedding usage
            await _usageTracker.TrackEmbeddingCallAsync(
                LlmOperationType.Embedding,
                requestId: context?.QueryLogId?.ToString() ?? Guid.NewGuid().ToString(),
                tokenCount: (int)(condensedQuestion.Length / 4.0),
                tenantId: context?.TenantId,
                companyId: context?.CompanyId);

            var cachedResult = await _queryCacheService.TryGetCachedSqlAsync(
                questionEmbedding,
                context?.TenantId ?? "default",
                context?.CompanyId ?? 0);

            if (cachedResult != null)
            {
                _logger.LogInformation("[SqlHandler] ✓ Cache HIT! Returing cached SQL.");

                // For cached results, provide minimal insights indicating cache source
                var cachedInsights = new List<string> { "⚡ Retrieved from Query Cache" };
                var cachedReasoning = $"This query was previously executed with {cachedResult.Score:P0} similarity. Serving cached SQL for faster response.";

                return await ExecutePipelineFromValidationAsync(
                    cachedResult.Sql,
                    context,
                    cachedResult.CacheId,
                    true, // isCached
                    cachedResult.Score,
                    null, // schemaContext
                    cachedResult.PageId, // NEW: Pass PageId from cache
                    cachedInsights, 
                    cachedReasoning);
            }
        }

        #endregion

        #region Phase 3: RAG Retrieval

        _logger.LogInformation("[SqlHandler] Layer 1: Retrieving context...");
        var retrievalResult = await _retrievalService.RetrieveContextAsync(
            condensedQuestion,
            context?.TenantId ?? "default",
            context?.CompanyId
        );

        #endregion

        #region Phase 3.5: Scope Guard (NEW)

        if (context?.PreviousPageId != null && retrievalResult.PageId != null && context.PreviousPageId != retrievalResult.PageId)
        {
            if (actionType == QueryActionType.WrapPrevious)
            {
                _logger.LogWarning("[SqlHandler] 🛡️ Scope Shift Detected! PreviousPageId={Prev} vs CurrentPageId={Curr}. Downgrading WrapPrevious -> Regenerate.", 
                    context.PreviousPageId, retrievalResult.PageId);
                actionType = QueryActionType.Regenerate;
            }
        }

        #endregion

        #region Phase 4: SQL Generation

        _logger.LogInformation("[SqlHandler] Layer 2: Generating SQL (Action: {ActionType})...", actionType);

        var sqlResult = await _generationService.GenerateSqlAsync(
            condensedQuestion,
            retrievalResult,
            context?.PreviousSql,
            actionType,
            context?.TenantId,
            context?.CompanyId
        );

        if (sqlResult.NeedsClarification)
        {
            return new QueryExecutionResult
            {
                ErrorMessage = $"CLARIFICATION_REQUIRED: {sqlResult.ClarificationQuestion}",
            };
        }

        if (string.IsNullOrWhiteSpace(sqlResult.Sql))
        {
            return new QueryExecutionResult { ErrorMessage = "Failed to generate SQL from the prompt." };
        }

        #endregion

        #region Phase 5: SQL Validation

        _logger.LogInformation("[SqlHandler] Layer 3: Validating SQL...");
        try
        {
            await _validatorService.ValidateSqlAsync(sqlResult.Sql, context?.TenantId ?? "default");
        }
        catch (Exception ex)
        {
            return new QueryExecutionResult { ErrorMessage = $"SQL Validation Failed: {ex.Message}" };
        }

        #endregion

        #region Phase 6: Cache Storage

        if (_queryCacheEnabled && questionEmbedding != null && !isCached && context?.QueryLogId != null)
        {
            var pageId = retrievalResult.PageId ?? 0;

            await _queryCacheService.StoreCachedSqlAsync(
                context.QueryLogId.Value,
                condensedQuestion,
                questionEmbedding,
                sqlResult.Sql,
                pageId,
                context?.TenantId ?? "default",
                context?.CompanyId ?? 0);
        }

        #endregion

        #region Phase 7: Execution Dispatch

        return await ExecutePipelineFromValidationAsync(
            sqlResult.Sql,
            context,
            null, // cacheId
            false, // isCached
            sqlResult.Confidence,
            retrievalResult.UnifiedPageContext,
            retrievalResult.PageId, // NEW: Pass PageId
            sqlResult.RetrievedContext, // NEW: Pass context for insights
            sqlResult.Reasoning); // NEW: Pass reasoning for insights

        #endregion
    }

    #endregion

    #region Private Methods

    private async Task<QueryExecutionResult> ExecutePipelineFromValidationAsync(
        string rawSql,
        IntentHandlerContext? context,
        string? cacheId,
        bool isCached,
        float? confidence,
        string? schemaContext,
        int? pageId = null, // NEW: Pass PageId
        List<string>? retrievedContext = null,
        string? reasoning = null)
    {
        // Double-check validation
        try
        {
            await _validatorService.ValidateSqlAsync(rawSql, context?.TenantId ?? "default");
        }
        catch (Exception ex)
        {
            // ============================================================================
            // AUTO-INVALIDATION: If cached SQL fails validation, remove it from cache!
            // ============================================================================
            if (isCached && !string.IsNullOrEmpty(cacheId))
            {
                _logger.LogWarning("[SqlHandler] ⚠️ Cached SQL failed validation. Invalidating cache entry: {CacheId}", cacheId);

                try
                {
                    await _queryCacheService.DeleteCacheEntryAsync(cacheId);
                    _logger.LogInformation("[SqlHandler] ✓ Cache entry invalidated successfully.");
                }
                catch (Exception cacheEx)
                {
                    _logger.LogError(cacheEx, "[SqlHandler] Failed to invalidate cache entry.");
                }
            }

            return new QueryExecutionResult
            {
                ErrorMessage = $"SQL Validation Failed: {ex.Message}. " +
                    (isCached ? "Bad cache entry has been removed. Please retry your question." : "")
            };
        }

        // 4. Layer 4: Rewriting (Security)
        _logger.LogInformation("[SqlHandler] Layer 4: Rewriting SQL...");
        var rewrittenResult = await _rewriterService.RewriteAsync(
            rawSql,
            context?.TenantId ?? "default",
            context?.CompanyId);

        // 5. Layer 5: Execution
        _logger.LogInformation("[SqlHandler] Layer 5: Executing SQL...");
        var executionResult = await _executionService.ExecuteSqlAsync(rewrittenResult.RewrittenSql, rewrittenResult.Parameters);

        // Cost tracking handled internally.

        return new QueryExecutionResult
        {
            RewrittenSql = rewrittenResult.RewrittenSql, // Store the final executed SQL
            Data = executionResult,
            IsCached = isCached,
            CacheId = cacheId,
            PageId = pageId, // NEW: Flow PageId back to orchestrator
            QueryLogId = context?.QueryLogId,
            SchemaContext = schemaContext,
            ConfidenceScore = confidence,
            QueryInsights = retrievedContext != null || reasoning != null
                ? new QueryInsightData
                {
                    RetrievedContext = retrievedContext,
                    Reasoning = reasoning ?? $"Generated SQL with {confidence:P0} confidence.",
                    IsReadOnly = true // SQL Handler always reads data
                }
                : null
        };
    }

    #endregion
}
