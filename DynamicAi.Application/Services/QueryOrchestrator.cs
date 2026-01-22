using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Exceptions;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using System.Threading;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// Implementation of IQueryOrchestrator.
    /// Orchestrates queries by delegating to specific IntentHandlers (Layered Architecture).
    /// </summary>
    public class QueryOrchestrator : IQueryOrchestrator
    {
        private readonly IIntentHandlerFactory _handlerFactory;
        private readonly IIntentClassificationService _intentClassifier;
        private readonly IIntentExampleManagementService _intentManager;
        private readonly IQueryLogRepository _queryLogRepo;
        private readonly IGeminiCostTracker _costTracker;
        private readonly ILogger<QueryOrchestrator> _logger;
        private readonly bool _queryCacheEnabled;

        public QueryOrchestrator(
            IIntentHandlerFactory handlerFactory,
            IIntentClassificationService intentClassifier,
            IIntentExampleManagementService intentExampleManagementService,
            IQueryLogRepository queryLogRepo,
            IGeminiCostTracker costTracker,
            ILogger<QueryOrchestrator> logger,
            IConfiguration configuration)
        {
            _handlerFactory = handlerFactory;
            _intentClassifier = intentClassifier;
            _intentManager = intentExampleManagementService;
            _queryLogRepo = queryLogRepo;
            _costTracker = costTracker;
            _logger = logger;
            _queryCacheEnabled = bool.Parse(configuration["Caching:QueryCache:Enabled"] ?? "true");
        }

        public async Task<QueryExecutionResult> HandleQueryAsync(
            string userQuestion,
            string tenantId,
            int? comID = null,
            string? previousQuestion = null,
            string? previousSql = null)
        {
            _logger.LogInformation("\n");
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║                  QUERY ORCHESTRATOR - PIPELINE START                   ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            _logger.LogInformation("[Orchestrator] User Question: \"{UserQuestion}\"", userQuestion);
            _logger.LogInformation("[Orchestrator] Tenant ID: {TenantId}", tenantId);

            var startTime = DateTime.UtcNow;
            Guid queryLogId = Guid.NewGuid();
            IntentClassificationResult? intentResult = null;
            QueryExecutionResult? result = null;

            try
            {
                // ============================================================================
                // Layer 0: INTENT CLASSIFICATION
                // ============================================================================
                _logger.LogInformation("[Orchestrator] Layer 0: Classifying Intent...");
                var intentStart = DateTime.UtcNow;
                intentResult = await _intentClassifier.ClassifyIntentAsync(userQuestion);
                var intentDuration = (DateTime.UtcNow - intentStart).TotalMilliseconds;

                _logger.LogInformation("[Orchestrator] ✓ Intent Detected: {Intent} (Confidence: {Confidence:F2}) in {Duration:F0}ms",
                    intentResult.Intent, intentResult.ConfidenceScore, intentDuration);

                // Save low confidence examples
                if (intentResult.ConfidenceScore < 0.75f)
                {
                    _ = _intentManager.StoreIntentExampleAsync(
                        question: userQuestion,
                        embedding: intentResult.QueryEmbedding,
                        intentType: intentResult.Intent,
                        cancellationToken: CancellationToken.None,
                        status: IntentReviewStatus.Pending,
                        source: "System_LowConfidenceFallback"
                    );
                }

                // ============================================================================
                // ROUTING TO HANDLER
                // ============================================================================
                if (_handlerFactory.TryGetHandler(intentResult.Intent, out var handler))
                {
                    _logger.LogInformation("[Orchestrator] Delegating to Handler: {HandlerType}", handler!.GetType().Name);

                    var context = new IntentHandlerContext
                    {
                        PreviousQuestion = previousQuestion,
                        PreviousSql = previousSql,
                        QueryLogId = queryLogId,
                        TenantId = tenantId,
                        CompanyId = comID
                    };

                    result = await handler.HandleAsync(userQuestion, intentResult, context);
                }
                else if (intentResult.Intent == IntentType.Chat)
                {
                    // Fallback for Chat until Module 3 is fully implemented
                    _logger.LogInformation("[Orchestrator] Chat intent detected (Fallback Logic) - returning friendly response");
                    result = new QueryExecutionResult
                    {
                        Data = new[] { new { Message = "مرحباً! أنا هنا لمساعدتك في استعلامات البيانات. كيف يمكنني مساعدتك اليوم؟", Type = "Chat" } },
                        QueryLogId = queryLogId,
                        IsCached = false
                    };
                }
                else
                {
                    _logger.LogWarning("[Orchestrator] No handler found for intent: {Intent}. Returning error.", intentResult.Intent);
                    throw new NotSupportedException($"The intent '{intentResult.Intent}' is not currently supported.");
                }

                // ============================================================================
                // POST-EXECUTION LOGGING
                // ============================================================================
                var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;

                if (result.IsSuccess && !result.IsCached)
                {
                    // Log success
                    var queryLog = new Domain.Entities.QueryLog
                    {
                        Id = queryLogId,
                        RawQuestion = userQuestion,
                        GeneratedSql = result.RewrittenSql, // Might be null for Chat
                        WasSuccessful = true,
                        SchemaContext = result.SchemaContext,
                        TenantId = tenantId,
                        CompanyId = comID,
                        CreatedAt = DateTime.UtcNow,
                        LatencyMs = (int)totalDuration,
                        HandlerType = intentResult.Intent.ToString(),
                        InputTokens = (int)_costTracker.GetSessionSummary().TotalInputTokens,
                        OutputTokens = (int)_costTracker.GetSessionSummary().TotalOutputTokens,
                        GeminiCost = (decimal)_costTracker.GetSessionSummary().TotalCostUsd,
                        ConfidenceScore = result.ConfidenceScore
                    };
                    // Fire and forget logging
                    _ = _queryLogRepo.AddAsync(queryLog);
                }
                else if (!result.IsSuccess)
                {
                    _logger.LogWarning("[Orchestrator] Handler returned failure: {Error}", result.ErrorMessage);

                    if (result.ErrorMessage?.StartsWith("CLARIFICATION_REQUIRED:") == true)
                    {
                        var clarification = result.ErrorMessage.Replace("CLARIFICATION_REQUIRED:", "").Trim();
                        throw new InteractionRequiredException(clarification, queryLogId, result.ConfidenceScore ?? 0);
                    }
                }

                _logger.LogInformation("[Orchestrator] Pipeline Completed in {Duration:F0}ms", totalDuration);
                return result;
            }
            catch (InteractionRequiredException)
            {
                throw; // Pass through controller
            }
            catch (Exception ex)
            {
                var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogError(ex, "[Orchestrator] Pipeline Failed after {Duration:F0}ms", totalDuration);

                // Log Failure
                var errorLog = new Domain.Entities.QueryLog
                {
                    Id = queryLogId,
                    RawQuestion = userQuestion,
                    WasSuccessful = false,
                    ErrorMessage = ex.Message,
                    TenantId = tenantId,
                    CompanyId = comID,
                    CreatedAt = DateTime.UtcNow,
                    LatencyMs = (int)totalDuration,
                    HandlerType = intentResult?.Intent.ToString() ?? "Unknown",
                    ConfidenceScore = intentResult?.ConfidenceScore
                };
                _ = _queryLogRepo.AddAsync(errorLog);

                throw;
            }
        }
    }
}
