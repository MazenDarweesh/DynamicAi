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
    private readonly IQueryCondenserService _condenserService;
    private readonly IRagRetrievalService _retrievalService;
    private readonly ISqlGenerationService _generationService;
    private readonly ISqlValidatorService _validatorService;
    private readonly ISqlRewriterService _rewriterService;
    private readonly ISandboxedSqlExecutor _executionService;
    private readonly IQueryCacheService _queryCacheService;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
    private readonly IGeminiCostTracker _costTracker; // For tracking token usage
    private readonly ILogger<SqlIntentHandler> _logger;
    private readonly bool _queryCacheEnabled;

    public SqlIntentHandler(
        IQueryCondenserService condenserService,
        IRagRetrievalService retrievalService,
        ISqlGenerationService generationService,
        ISqlValidatorService validatorService,
        ISqlRewriterService rewriterService,
        ISandboxedSqlExecutor executionService,
        IQueryCacheService queryCacheService,
        IEmbeddingGenerator<string, Embedding<float>> embeddingService,
        IGeminiCostTracker costTracker,
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
        _costTracker = costTracker;
        _logger = logger;
        _queryCacheEnabled = bool.Parse(configuration["Caching:QueryCache:Enabled"] ?? "true");
    }

    public IntentType[] SupportedIntents => new[] { IntentType.DataRetrieval, IntentType.Analysis };

    public bool CanHandle(IntentType intent) => SupportedIntents.Contains(intent);

    public async Task<QueryExecutionResult> HandleAsync(
        string userQuestion,
        IntentClassificationResult intentResult,
        IntentHandlerContext? context = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[SqlHandler] Starting SQL pipeline for intent: {Intent}", intentResult.Intent);

        // 0.5 Layer 0.5: Query Condensing
        _logger.LogInformation("[SqlHandler] Layer 0.5: Condensing Query...");
        var condenserResult = await _condenserService.CondenseQuestionAsync(
            userQuestion,
            context?.PreviousQuestion,
            context?.PreviousSql);

        var condensedQuestion = condenserResult.CondensedQuestion;
        var actionType = condenserResult.ActionType;

        // 0. Layer 0: Query Cache Check
        float[]? questionEmbedding = null;
        bool isCached = false;

        if (_queryCacheEnabled)
        {
            _logger.LogInformation("[SqlHandler] Layer 0: Checking Query Cache...");
            var embedding = await _embeddingService.GenerateAsync(condensedQuestion);
            questionEmbedding = embedding.Vector.ToArray();

            var cachedResult = await _queryCacheService.TryGetCachedSqlAsync(
                questionEmbedding,
                context?.TenantId ?? "default",
                context?.CompanyId ?? 0);

            if (cachedResult != null)
            {
                _logger.LogInformation("[SqlHandler] ✓ Cache HIT! Returing cached SQL.");

                return await ExecutePipelineFromValidationAsync(
                    cachedResult.Sql,
                    context,
                    cachedResult.CacheId,
                    true, // isCached
                    cachedResult.Score,
                    null);
            }
        }

        // 1. Layer 1: RAG Retrieval
        _logger.LogInformation("[SqlHandler] Layer 1: Retrieving context...");
        var retrievalResult = await _retrievalService.RetrieveContextAsync(
            condensedQuestion,
            context?.TenantId ?? "default"
        );

        // 2. Layer 2: SQL Generation
        _logger.LogInformation("[SqlHandler] Layer 2: Generating SQL (Action: {ActionType})...", actionType);

        var sqlResult = await _generationService.GenerateSqlAsync(
            condensedQuestion,
            retrievalResult,
            context?.PreviousSql,
            actionType
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

        // 3. Layer 3: Validation
        _logger.LogInformation("[SqlHandler] Layer 3: Validating SQL...");
        try
        {
            await _validatorService.ValidateSqlAsync(sqlResult.Sql, context?.TenantId ?? "default");
        }
        catch (Exception ex)
        {
            return new QueryExecutionResult { ErrorMessage = $"SQL Validation Failed: {ex.Message}" };
        }

        // 3.5 Cache Store
        if (_queryCacheEnabled && questionEmbedding != null && !isCached && context?.QueryLogId != null)
        {
            var pageId = 0;

            await _queryCacheService.StoreCachedSqlAsync(
                context.QueryLogId.Value,
                condensedQuestion,
                questionEmbedding,
                sqlResult.Sql,
                pageId,
                context?.TenantId ?? "default",
                context?.CompanyId ?? 0);
        }

        return await ExecutePipelineFromValidationAsync(
            sqlResult.Sql,
            context,
            null, // cacheId
            false, // isCached
            sqlResult.Confidence,
            retrievalResult.UnifiedPageContext);
    }

    private async Task<QueryExecutionResult> ExecutePipelineFromValidationAsync(
        string rawSql,
        IntentHandlerContext? context,
        string? cacheId,
        bool isCached,
        float? confidence,
        string? schemaContext)
    {
        // Double-check validation
        try
        {
            await _validatorService.ValidateSqlAsync(rawSql, context?.TenantId ?? "default");
        }
        catch (Exception ex)
        {
            return new QueryExecutionResult { ErrorMessage = $"SQL Validation Failed (Pipeline): {ex.Message}" };
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
            QueryLogId = context?.QueryLogId,
            SchemaContext = schemaContext,
            ConfidenceScore = confidence
        };
    }
}
