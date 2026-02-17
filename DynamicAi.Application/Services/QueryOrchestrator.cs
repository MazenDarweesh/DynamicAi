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
        private readonly ILlmUsageTracker _usageTracker;
        private readonly ILogger<QueryOrchestrator> _logger;
        private readonly IConversationContextService _contextService;
        private readonly IFeedbackService _feedbackService;
        private readonly bool _queryCacheEnabled;

        #endregion

        #region Constructor

        public QueryOrchestrator(
            IIntentHandlerFactory handlerFactory,
            IIntentClassificationService intentClassifier,
            IIntentExampleManagementService intentExampleManagementService,
            IQueryLogRepository queryLogRepo,
            IConversationContextService contextService,
            IFeedbackService feedbackService,
            ILlmUsageTracker usageTracker,
            ILogger<QueryOrchestrator> logger,
            IConfiguration configuration)
        {
            _handlerFactory = handlerFactory;
            _contextService = contextService;
            _feedbackService = feedbackService;
            _intentClassifier = intentClassifier;
            _intentManager = intentExampleManagementService;
            _queryLogRepo = queryLogRepo;
            _usageTracker = usageTracker;
            _logger = logger;
            _queryCacheEnabled = !bool.TryParse(configuration["Caching:QueryCache:Enabled"], out var enabled) || enabled; // Default true
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


                #region Layer 0: Intent Classification

                _logger.LogInformation("[Orchestrator] Layer 0: Classifying Intent...");
                var intentStart = DateTime.UtcNow;
                // ✅ Pass ONLY the clean user question — not the SQL-augmented context
                intentResult = await _intentClassifier.ClassifyIntentAsync(userQuestion);
                var intentDuration = (DateTime.UtcNow - intentStart).TotalMilliseconds;

                _logger.LogInformation("[Orchestrator] ✓ Intent Detected: {Intent} (Confidence: {Confidence:F2}) in {Duration:F0}ms",
                    intentResult.Intent, intentResult.ConfidenceScore, intentDuration);

                // [USER_REQUEST]: Save ALL queries for review in this stage (Data Gathering)
                // if (intentResult.ConfidenceScore < 0.75f) <--- Disabled for now
                {
                    _ = _intentManager.StoreIntentExampleAsync(
                        question: userQuestion,
                        embedding: intentResult.QueryEmbedding,
                        intentType: intentResult.Intent,
                        cancellationToken: CancellationToken.None,
                        status: DynamicAi.Domain.Entities.IntentReviewStatus.Pending, // Ensure full qualification
                        source: "System_AllQueriesCapture"
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
                        InputTokens = (int)_usageTracker.GetSessionSummary().TotalInputTokens,
                        OutputTokens = (int)_usageTracker.GetSessionSummary().TotalOutputTokens,
                        GeminiCost = (decimal)_usageTracker.GetSessionSummary().TotalCostUsd,
                        ConfidenceScore = result.ConfidenceScore
                    };

                    // Await logging to prevent DbContext disposal before SaveChanges completes
                    try
                    {
                        await _queryLogRepo.AddAsync(queryLog);
                        _logger.LogDebug("[Orchestrator] Query log saved: {LogId}", queryLogId);

                        // 📋 Auto-Capture for Training Dataset (M13)
                        // This ensures every successful AI generation is captured for review
                        _logger.LogInformation("[Orchestrator] Triggering Auto-Capture for LogId: {LogId}, ComId: {ComId}", queryLogId, comID);
                        var captureResult = await _feedbackService.AutoCaptureExampleAsync(queryLogId, comID);
                        _logger.LogInformation("[Orchestrator] Auto-Capture result: {Result}", captureResult);
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

                if (result != null && intentResult != null)
                {
                    result.Intent = intentResult.Intent.ToString();
                }
                return result!;

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

        #endregion



        #region Multi-Step Prediction Pipeline

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
            var pipelineStart = DateTime.UtcNow;

            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║     [MULTI-STEP PIPELINE] PredictionWithDataFetch - Starting          ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("[MultiStep] 📝 Original Question: \"{UserQuestion}\"", userQuestion);
            _logger.LogInformation("[MultiStep] 🆔 Session: {SessionId}", sessionId);

            // Step 1: Extract data query
            _logger.LogInformation("");
            _logger.LogInformation("═══════════════ STEP 1: QUERY EXTRACTION ═══════════════");
            var extractStart = DateTime.UtcNow;
            var dataQuery = ExtractDataQueryFromCombined(userQuestion);
            var extractDuration = (DateTime.UtcNow - extractStart).TotalMilliseconds;
            _logger.LogInformation("[MultiStep] ✓ Extracted Data Query: \"{DataQuery}\"", dataQuery);
            _logger.LogInformation("[MultiStep] ⏱️ Extraction Time: {Duration:F0}ms", extractDuration);

            // Step 2: Fetch data via SqlIntentHandler
            _logger.LogInformation("");
            _logger.LogInformation("═══════════════ STEP 2: DATA RETRIEVAL ═══════════════");
            var fetchStart = DateTime.UtcNow;

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
            var fetchDuration = (DateTime.UtcNow - fetchStart).TotalMilliseconds;

            if (!dataResult.IsSuccess || dataResult.Data == null)
            {
                _logger.LogWarning("[MultiStep] ⚠️ Data fetch failed. Proceeding with prediction anyway...");
                _logger.LogWarning("[MultiStep] Error: {Error}", dataResult.ErrorMessage ?? "Unknown");
            }
            else
            {
                // Log data statistics
                var dataCount = GetDataCount(dataResult.Data);
                var sqlLength = dataResult.RewrittenSql?.Length ?? 0;

                _logger.LogInformation("[MultiStep] ✓ Data fetched successfully in {Duration:F0}ms", fetchDuration);
                _logger.LogInformation("[MultiStep] 📊 Data Statistics:");
                _logger.LogInformation("[MultiStep]   └─ Rows Retrieved: {Count}", dataCount);
                _logger.LogInformation("[MultiStep]   └─ SQL Length: {SqlLength} chars", sqlLength);

                // Save to context
                _logger.LogInformation("[MultiStep] 💾 Saving data to conversation context...");
                await _contextService.SaveTurnAsync(
                    sessionId,
                    dataQuery,
                    "DataRetrieval",
                    dataResult.RewrittenSql,
                    dataResult.Data,
                    cancellationToken);
                _logger.LogInformation("[MultiStep] ✓ Context saved for session: {SessionId}", sessionId);
            }

            // Step 3: Execute Prediction
            _logger.LogInformation("");
            _logger.LogInformation("═══════════════ STEP 3: PREDICTION GENERATION ═══════════════");
            var predictStart = DateTime.UtcNow;

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

            _logger.LogInformation("[MultiStep] 🔮 Calling PredictIntentHandler...");
            var predictionResult = await predictHandler!.HandleAsync(userQuestion, predictIntentResult, predictContext, cancellationToken);
            var predictDuration = (DateTime.UtcNow - predictStart).TotalMilliseconds;

            // Final Summary
            var totalDuration = (DateTime.UtcNow - pipelineStart).TotalMilliseconds;
            _logger.LogInformation("");
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║     [MULTI-STEP PIPELINE] PredictionWithDataFetch - Completed ✓       ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("[MultiStep] ═══════════════ TIMING BREAKDOWN ═══════════════");
            _logger.LogInformation("[MultiStep]   Step 1 (Extraction):   {Duration,8:F0}ms", extractDuration);
            _logger.LogInformation("[MultiStep]   Step 2 (Data Fetch):   {Duration,8:F0}ms", fetchDuration);
            _logger.LogInformation("[MultiStep]   Step 3 (Prediction):   {Duration,8:F0}ms", predictDuration);
            _logger.LogInformation("[MultiStep]   ─────────────────────────────────────");
            _logger.LogInformation("[MultiStep]   TOTAL PIPELINE:        {Duration,8:F0}ms", totalDuration);

            predictionResult.Intent = intentResult.Intent.ToString();
            return predictionResult;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Helper to count rows in data result
        /// </summary>
        private static int GetDataCount(object? data)
        {
            if (data == null) return 0;
            if (data is System.Collections.IEnumerable enumerable)
            {
                var count = 0;
                foreach (var _ in enumerable) count++;
                return count;
            }
            return 1;
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
