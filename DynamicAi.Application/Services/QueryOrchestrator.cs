using System.Text;
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
        #region Fields

        private readonly IIntentHandlerFactory _handlerFactory;
        private readonly IIntentClassificationService _intentClassifier;
        private readonly IIntentExampleManagementService _intentManager;
        private readonly IQueryLogRepository _queryLogRepo;
        private readonly IGeminiCostTracker _costTracker;
        private readonly ILogger<QueryOrchestrator> _logger;
        private readonly IConversationContextService _contextService;
        private readonly bool _queryCacheEnabled;

        #endregion

        #region Constructor

        public QueryOrchestrator(
            IIntentHandlerFactory handlerFactory,
            IIntentClassificationService intentClassifier,
            IIntentExampleManagementService intentExampleManagementService,
            IQueryLogRepository queryLogRepo,
            IConversationContextService contextService,
            IGeminiCostTracker costTracker,
            ILogger<QueryOrchestrator> logger,
            IConfiguration configuration)
        {
            _handlerFactory = handlerFactory;
            _contextService = contextService;
            _intentClassifier = intentClassifier;
            _intentManager = intentExampleManagementService;
            _queryLogRepo = queryLogRepo;
            _costTracker = costTracker;
            _logger = logger;
            _queryCacheEnabled = bool.Parse(configuration["Caching:QueryCache:Enabled"] ?? "true");
        }

        #endregion

        #region Public Methods

        public async Task<QueryExecutionResult> HandleQueryAsync(
            string userQuestion,
            string sessionId,
            string tenantId,
            int? comID = null,
            string? previousQuestion = null,
            string? previousSql = null)
        {
            #region Setup & Logging

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

            #endregion

            try
            {
                #region Context Injection (Layer -1)

                var recentContext = await _contextService.GetRecentContextAsync(sessionId);
                var augmentedQuestion = BuildPromptWithContext(userQuestion, recentContext);

                // Use augmented question for classification and handling to ensure context awareness
                var questionToProcess = augmentedQuestion;

                #endregion

                #region Layer 0: Intent Classification

                _logger.LogInformation("[Orchestrator] Layer 0: Classifying Intent...");
                var intentStart = DateTime.UtcNow;
                // We pass the augmented question so the classifier sees "Previous: SQL... Current: Why low?"
                intentResult = await _intentClassifier.ClassifyIntentAsync(questionToProcess);
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

                #endregion

                #region Routing to Handler

                // Phase 5: Multi-Step Handling for PredictionWithDataFetch
                if (intentResult.Intent == IntentType.PredictionWithDataFetch)
                {
                    _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
                    _logger.LogInformation("║           [MULTI-STEP] PredictionWithDataFetch - Starting              ║");
                    _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

                    result = await HandleMultiStepPredictionAsync(
                        userQuestion,
                        sessionId,
                        tenantId,
                        comID,
                        previousQuestion,
                        previousSql,
                        queryLogId,
                        intentResult,
                        CancellationToken.None);
                }
                else if (_handlerFactory.TryGetHandler(intentResult.Intent, out var handler))
                {
                    _logger.LogInformation("[Orchestrator] Delegating to Handler: {HandlerType}", handler!.GetType().Name);

                    var context = new IntentHandlerContext
                    {
                        PreviousQuestion = previousQuestion,
                        PreviousSql = previousSql,
                        QueryLogId = queryLogId,
                        TenantId = tenantId,
                        CompanyId = comID,
                        SessionId = sessionId  // Phase 4: For PredictIntentHandler context retrieval
                    };

                    result = await handler.HandleAsync(userQuestion, intentResult, context);
                }
                else
                {
                    // No handler registered for this intent type
                    _logger.LogWarning("[Orchestrator] No handler found for intent: {Intent}. Returning error.", intentResult.Intent);
                    throw new NotSupportedException($"The intent '{intentResult.Intent}' is not currently supported.");
                }

                #endregion

                #region Post-Execution Logging

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

                    // Await logging to prevent DbContext disposal before SaveChanges completes
                    try
                    {
                        await _queryLogRepo.AddAsync(queryLog);
                        _logger.LogDebug("[Orchestrator] Query log saved: {LogId}", queryLogId);
                    }
                    catch (Exception logEx)
                    {
                        _logger.LogWarning(logEx, "[Orchestrator] Failed to save query log, pipeline continues");
                    }
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
                #region Context Saving

                if (result != null && result.IsSuccess)
                {
                    // Save the turn asynchronously (fire and forget-ish, but awaited)
                    await _contextService.SaveTurnAsync(
                        sessionId,
                        userQuestion, // Save original question
                        intentResult?.Intent.ToString() ?? "Unknown",
                        result.RewrittenSql, // Or executed SQL
                        result.Data // The service will handle sampling
                    );
                }

                #endregion

                return result;

                #endregion
            }
            #region Error Handling

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

                // Await logging to prevent DbContext disposal before SaveChanges completes
                try
                {
                    await _queryLogRepo.AddAsync(errorLog);
                    _logger.LogDebug("[Orchestrator] Error log saved: {LogId}", queryLogId);
                }
                catch (Exception logSaveEx)
                {
                    _logger.LogWarning(logSaveEx, "[Orchestrator] Failed to save error log");
                }

                throw;
            }

            #endregion
        }

        private string BuildPromptWithContext(string question, List<ConversationTurn> context)
        {
            if (context == null || !context.Any())
                return question;

            var sb = new StringBuilder();
            sb.AppendLine("CONVERSATION HISTORY (Youngest to Oldest):");
            sb.AppendLine("---");

            foreach (var turn in context)
            {
                sb.AppendLine($"User: {turn.Question}");
                if (!string.IsNullOrEmpty(turn.SqlExecuted))
                    sb.AppendLine($"Executed SQL: {turn.SqlExecuted}");
                if (!string.IsNullOrEmpty(turn.ResultSampleJson))
                    sb.AppendLine($"Result Sample: {turn.ResultSampleJson}");
                sb.AppendLine();
            }
            sb.AppendLine("---");
            sb.AppendLine($"CURRENT QUESTION: {question}");

            return sb.ToString();
        }

        /// <summary>
        /// Handles Multi-Step prediction queries like "Based on last 5 months data, predict next month sales"
        /// Step 1: Extract and execute the data fetch query
        /// Step 2: Save results to conversation context
        /// Step 3: Execute prediction using the fetched data
        /// </summary>
        private async Task<QueryExecutionResult> HandleMultiStepPredictionAsync(
            string userQuestion,
            string sessionId,
            string tenantId,
            int? comID,
            string? previousQuestion,
            string? previousSql,
            Guid queryLogId,
            IntentClassificationResult intentResult,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("[MultiStep] Step 1: Extracting data query from combined question...");

            // Extract the data part from the question
            var dataQuery = ExtractDataQueryFromCombined(userQuestion);
            _logger.LogInformation("[MultiStep] Extracted Data Query: \"{DataQuery}\"", dataQuery);

            // Step 1: Call SqlIntentHandler to fetch data
            _logger.LogInformation("[MultiStep] Step 2: Fetching data via SqlIntentHandler...");

            if (!_handlerFactory.TryGetHandler(IntentType.DataRetrieval, out var sqlHandler))
            {
                _logger.LogError("[MultiStep] ❌ SqlIntentHandler not found!");
                throw new InvalidOperationException("SqlIntentHandler is required for Multi-Step prediction");
            }

            var sqlContext = new IntentHandlerContext
            {
                PreviousQuestion = previousQuestion,
                PreviousSql = previousSql,
                QueryLogId = queryLogId,
                TenantId = tenantId,
                CompanyId = comID,
                SessionId = sessionId
            };

            var sqlIntentResult = new IntentClassificationResult
            {
                Intent = IntentType.DataRetrieval,
                ConfidenceScore = 0.95f,
                MatchedExample = "Multi-Step Data Fetch"
            };

            var dataResult = await sqlHandler!.HandleAsync(dataQuery, sqlIntentResult, sqlContext, cancellationToken);

            if (!dataResult.IsSuccess || dataResult.Data == null)
            {
                _logger.LogWarning("[MultiStep] ⚠️ Data fetch failed. Proceeding with prediction anyway...");
            }
            else
            {
                _logger.LogInformation("[MultiStep] ✓ Data fetched successfully. Saving to context...");

                // Step 2: Save the data to conversation context for the prediction handler
                await _contextService.SaveTurnAsync(
                    sessionId,
                    dataQuery,
                    "DataRetrieval",
                    dataResult.RewrittenSql,
                    dataResult.Data,
                    cancellationToken);
            }

            // Step 3: Call PredictIntentHandler with the context now populated
            _logger.LogInformation("[MultiStep] Step 3: Executing prediction with fetched context...");

            if (!_handlerFactory.TryGetHandler(IntentType.Prediction, out var predictHandler))
            {
                _logger.LogError("[MultiStep] ❌ PredictIntentHandler not found!");
                throw new InvalidOperationException("PredictIntentHandler is required for Multi-Step prediction");
            }

            var predictIntentResult = new IntentClassificationResult
            {
                Intent = IntentType.Prediction,
                ConfidenceScore = 0.95f,
                MatchedExample = "Multi-Step Prediction"
            };

            var predictContext = new IntentHandlerContext
            {
                PreviousQuestion = dataQuery,
                PreviousSql = dataResult?.RewrittenSql,
                QueryLogId = queryLogId,
                TenantId = tenantId,
                CompanyId = comID,
                SessionId = sessionId
            };

            var predictionResult = await predictHandler!.HandleAsync(userQuestion, predictIntentResult, predictContext, cancellationToken);

            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║           [MULTI-STEP] PredictionWithDataFetch - Completed ✓          ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            return predictionResult;
        }

        /// <summary>
        /// Extracts the data-related part from a combined prediction query.
        /// Example: "بناء على بيانات اخر 5 شهور توقع لي المبيعات" → "هات بيانات اخر 5 شهور"
        /// </summary>
        private static string ExtractDataQueryFromCombined(string combinedQuestion)
        {
            var question = combinedQuestion.ToLowerInvariant();

            // Arabic patterns: "بناء على X توقع Y" → extract X
            var arabicPatterns = new[]
            {
                ("بناء على", "توقع"),
                ("بناءً على", "توقع"),
                ("استناداً", "توقع"),
                ("من بيانات", "توقع"),
                ("based on", "predict"),
                ("using data", "predict"),
                ("using", "forecast")
            };

            foreach (var (startMarker, endMarker) in arabicPatterns)
            {
                var startIndex = question.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
                var endIndex = question.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);

                if (startIndex >= 0 && endIndex > startIndex)
                {
                    var dataPartStart = startIndex + startMarker.Length;
                    var dataPart = combinedQuestion[dataPartStart..endIndex].Trim();

                    // Clean up and form a proper query
                    if (!string.IsNullOrEmpty(dataPart))
                    {
                        // Add "هات" or "Get" prefix to make it a proper data query
                        var isArabic = dataPart.Any(c => c >= '\u0600' && c <= '\u06FF');
                        return isArabic ? $"هات {dataPart}" : $"Get {dataPart}";
                    }
                }
            }

            // Fallback: Try to extract time-based data references
            var timePatterns = new[] { "اخر", "آخر", "السابق", "الماضي", "last", "previous", "past" };
            foreach (var pattern in timePatterns)
            {
                var index = question.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    // Extract a reasonable chunk around the time pattern
                    var startPos = Math.Max(0, index);
                    var endPos = Math.Min(combinedQuestion.Length, index + 30);
                    var chunk = combinedQuestion[startPos..endPos].Trim();

                    var isArabic = chunk.Any(c => c >= '\u0600' && c <= '\u06FF');
                    return isArabic ? $"هات بيانات {chunk}" : $"Get data for {chunk}";
                }
            }

            // Ultimate fallback: return generic data request
            return combinedQuestion.Any(c => c >= '\u0600' && c <= '\u06FF')
                ? "هات البيانات المتاحة"
                : "Get available data";
        }

        #endregion
    }
}
