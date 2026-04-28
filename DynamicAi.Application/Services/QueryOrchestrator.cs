using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Exceptions;
using DynamicAi.Domain.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using System.Threading;
using DynamicAi.Application.Models.DirectIntegration;

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
        private readonly IQueryCondenserService _condenserService;
        private readonly IQueryCacheService _queryCacheService;
        private readonly IQueryLogRepository _queryLogRepo;
        private readonly ILlmUsageTracker _usageTracker;
        private readonly ILogger<QueryOrchestrator> _logger;
        private readonly IConversationContextService _contextService;
        private readonly IFeedbackService _feedbackService;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
        private readonly bool _queryCacheEnabled;
        private readonly bool _condenserEnabled;
        private readonly bool _intentClassificationEnabled;
        private readonly IServiceScopeFactory _scopeFactory;

        #endregion

        #region Constructor

        public QueryOrchestrator(
            IIntentHandlerFactory handlerFactory,
            IIntentClassificationService intentClassifier,
            IIntentExampleManagementService intentExampleManagementService,
            IQueryCondenserService condenserService,
            IQueryCacheService queryCacheService,
            IQueryLogRepository queryLogRepo,
            IConversationContextService contextService,
            IFeedbackService feedbackService,
            ILlmUsageTracker usageTracker,
            IEmbeddingGenerator<string, Embedding<float>> embeddingService,
            ILogger<QueryOrchestrator> logger,
            IConfiguration configuration,
            IServiceScopeFactory scopeFactory)
        {
            _handlerFactory = handlerFactory;
            _contextService = contextService;
            _feedbackService = feedbackService;
            _intentClassifier = intentClassifier;
            _intentManager = intentExampleManagementService;
            _condenserService = condenserService;
            _queryCacheService = queryCacheService;
            _queryLogRepo = queryLogRepo;
            _usageTracker = usageTracker;
            _embeddingService = embeddingService;
            _logger = logger;
            _scopeFactory = scopeFactory;
            _queryCacheEnabled = !bool.TryParse(configuration["Caching:QueryCache:Enabled"], out var enabled) || enabled; // Default true
            _condenserEnabled = !bool.TryParse(configuration["Features:CondenserEnabled"], out var condEnabled) || condEnabled; // Default true
            _intentClassificationEnabled = !bool.TryParse(configuration["Features:IntentClassificationEnabled"], out var intentEnabled) || intentEnabled; // Default true
        }

        #endregion

        #region Public Methods

        public async Task<QueryExecutionResult> HandleQueryAsync(
            string userQuestion,
            string sessionId,
            string tenantId,
            int? comID = null,
            string? previousQuestion = null,
            string? previousSql = null,
            int? explicitPageId = null,     // [NEW]
            CancellationToken ct = default)
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
                #region Context Loading (Phase 0)

                _logger.LogInformation("[Orchestrator] Loading recent context for session: {SessionId}", sessionId);
                var recentTurns = await _contextService.GetRecentContextAsync(sessionId, limit: 3, pageId: null, ct);
                var lastTurn = recentTurns.FirstOrDefault();
                var previousPageId = lastTurn?.PageId;

                if (lastTurn != null)
                {
                    _logger.LogInformation("[Orchestrator] Prior context found. PrevQuestion=\"{PrevQ}\", PrevPageId={PrevPageId}",
                        lastTurn.Question, previousPageId);
                }

                #endregion

                #region Layer 0: Intent Classification + Query Condensing (PARALLEL)

                _logger.LogInformation("[Orchestrator] Layer 0: Computing shared embedding, then starting Intent + Condenser in PARALLEL...");
                var parallelStart = DateTime.UtcNow;

                // Step 0.1: Compute embedding ONCE (always needed for either Intent Classification, RAG Context Retrieval, or Data Gathering)
                var embeddingStart = System.Diagnostics.Stopwatch.StartNew();
                var sharedEmbedding = await _embeddingService.GenerateAsync(userQuestion, cancellationToken: ct);
                float[] sharedVector = sharedEmbedding.Vector.ToArray();
                embeddingStart.Stop();
                _logger.LogInformation("[Orchestrator] ✓ Shared embedding computed in {Duration:F0}ms (will be reused by Intent + RAG)", embeddingStart.ElapsedMilliseconds);

                // --- NEW CACHE CHECK ---
                CachedQueryResult? cachedResult = null;
                if (_queryCacheEnabled)
                {
                    _logger.LogInformation("[Orchestrator] Layer 0.5: Checking Global Query Cache...");
                    cachedResult = await _queryCacheService.TryGetCachedSqlAsync(
                        sharedVector,
                        tenantId,
                        comID ?? 1,
                        explicitPageId,  // 👉 [NEW] Pass Explicit PageId!
                        ct);

                    if (cachedResult != null)
                    {
                        _logger.LogInformation("[Orchestrator] ⚡ Global Query Cache HIT! CacheId: {CacheId}", cachedResult.CacheId);
                    }
                }

                // [Phantom Fix] Step 0.2: Smart dispatch — checks flags BEFORE creating tasks.
                // Branches:
                //   A. Both enabled  → TRUE PARALLEL via Task.WhenAll
                //   B. Intent only   → await intent directly (no WhenAll overhead)
                //   C. Condenser only → await condenser directly
                //   D. Both disabled → pure synchronous new() — zero thread pool touch
                bool intentActive    = _intentClassificationEnabled && cachedResult == null;
                bool condenserActive = _condenserEnabled;

                IntentClassificationResult intentResult_p;
                CondenserResult            condenserResult_p;

                if (intentActive && condenserActive)
                {
                    // ✅ TRUE PARALLEL: Both services are live — Task.WhenAll is correct here
                    _logger.LogInformation("[Orchestrator] ⚡ Both Intent + Condenser enabled → TRUE PARALLEL");
                    var intentTask_p    = _intentClassifier.ClassifyIntentAsync(userQuestion, sharedVector, cancellationToken: ct);
                    var condenserTask_p = _condenserService.CondenseQuestionAsync(userQuestion, previousQuestion, previousSql, tenantId, comID, ct);
                    await Task.WhenAll(intentTask_p, condenserTask_p);
                    intentResult_p    = intentTask_p.Result;
                    condenserResult_p = condenserTask_p.Result;
                }
                else if (intentActive)
                {
                    // 🔵 INTENT ONLY: Condenser is disabled — await intent directly, assign condenser default
                    _logger.LogInformation("[Orchestrator] 🚀 Condenser bypassed (disabled in config). Awaiting Intent only.");
                    intentResult_p    = await _intentClassifier.ClassifyIntentAsync(userQuestion, sharedVector, cancellationToken: ct);
                    condenserResult_p = new CondenserResult { ActionType = QueryActionType.New, CondensedQuestion = userQuestion };
                }
                else if (condenserActive)
                {
                    // 🟡 CONDENSER ONLY: Intent disabled or cache-hit — await condenser directly
                    _logger.LogInformation("[Orchestrator] 🚀 Intent bypassed (disabled in config or cache hit). Awaiting Condenser only.");
                    condenserResult_p = await _condenserService.CondenseQuestionAsync(userQuestion, previousQuestion, previousSql, tenantId, comID, ct);
                    intentResult_p    = new IntentClassificationResult
                    {
                        Intent         = IntentType.Unknown,
                        ConfidenceScore = 1.0f,
                        QueryEmbedding = sharedVector
                    };
                }
                else
                {
                    // ⚡ ZERO ALLOCATION: Both disabled/bypassed — no thread pool touch at all
                    _logger.LogInformation("[Orchestrator] ⚡ Both Intent + Condenser bypassed (disabled or cache hit). Zero async allocation.");
                    intentResult_p    = new IntentClassificationResult
                    {
                        Intent         = IntentType.Unknown,
                        ConfidenceScore = 1.0f,
                        QueryEmbedding = sharedVector
                    };
                    condenserResult_p = new CondenserResult { ActionType = QueryActionType.New, CondensedQuestion = userQuestion };
                }

                intentResult        = intentResult_p;
                var condenserResult = condenserResult_p;

                var parallelDuration = (DateTime.UtcNow - parallelStart).TotalMilliseconds;
                _logger.LogInformation("[Orchestrator] ✓ Layer 0 (Intent + Condenser) resolved in {Duration:F0}ms", parallelDuration);
                _logger.LogInformation("[Orchestrator]   → Intent: {Intent} (Confidence: {Confidence:F2})",
                    intentResult.Intent, intentResult.ConfidenceScore);

                if (condenserActive && cachedResult == null)
                {
                    _logger.LogInformation("[Orchestrator]   → Condenser: Action={Action}, Question=\"{Question}\"",
                        condenserResult.ActionType, condenserResult.CondensedQuestion);
                }

                // [USER_REQUEST]: Save ALL queries for review in this stage (Data Gathering)
                // Stop seeding "Unknown" intents which pollute the DB
                if (intentResult.Intent != IntentType.Unknown)
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
                
                ct.ThrowIfCancellationRequested(); // ✂️ Checkpoint after Layer 0 (Intent/Condenser)

                #region Routing to Handler

                // Phase 5: Multi-Step Handling for PredictionWithDataFetch (Only if Intent Classification is enabled)
                if (_intentClassificationEnabled && intentResult.Intent == IntentType.PredictionWithDataFetch)
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
                        ct);
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
                        SessionId = sessionId,  // Phase 4: For PredictIntentHandler context retrieval
                        PreviousPageId = previousPageId,
                        PrecomputedCondenserResult = condenserResult, // P1-2: Pass precomputed condenser result
                        ConversationHistory = recentTurns,
                        IsCondenserBypassed = !_condenserEnabled,
                        PrecomputedEmbedding = sharedVector,
                        ExplicitPageId = explicitPageId,     // 👉 [NEW] Pass Explicit PageId!
                        CachedSql = cachedResult?.Sql,       // 👉 Global Cache SQL Hit
                        CachedPageId = cachedResult?.PageId, // 👉 Global Cache Page Hit
                        CachedIntent = cachedResult?.Intent, // 🎯 Pass original intent
                        CachedCacheId = cachedResult?.CacheId // 🆔 Pass original Qdrant Entry ID for precise invalidation
                    };

                    ct.ThrowIfCancellationRequested(); // ✂️ Checkpoint before Handler delegation

                    result = await handler.HandleAsync(userQuestion, intentResult, context, ct);

                    // 🔮 [AGENTIC 2-PASS PIPELINE] Delegate to shared helper
                    if (result.IsSuccess && result.Intent == "PredictionWithDataRetrieval")
                    {
                        await ExecuteAgenticPredictionPassAsync(
                            result, userQuestion, previousQuestion,
                            sessionId, tenantId, comID, queryLogId,
                            recentTurns, condenserResult, ct);
                    }
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
                    // 🚀 Fire-and-forget success logging via Task.Run to save ~500ms API LATENCY
                    var inputTokens = (int)_usageTracker.GetSessionSummary().TotalInputTokens;
                    var outputTokens = (int)_usageTracker.GetSessionSummary().TotalOutputTokens;
                    var geminiCost = (decimal)_usageTracker.GetSessionSummary().TotalCostUsd;
                    var schemaContext = result.SchemaContext;
                    var rewrittenSql = result.RewrittenSql;
                    var handlerType = !string.IsNullOrEmpty(result.Intent) && result.Intent != "Unknown" ? result.Intent : intentResult?.Intent.ToString() ?? "Unknown";
                    var confidenceScore = result.ConfidenceScore;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var queryLogRepo = scope.ServiceProvider.GetRequiredService<IQueryLogRepository>();
                            var feedbackService = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
                            var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<QueryOrchestrator>>();

                            var queryLog = new Domain.Entities.QueryLog
                            {
                                Id = queryLogId,
                                RawQuestion = userQuestion,
                                GeneratedSql = rewrittenSql,
                                WasSuccessful = true,
                                SchemaContext = schemaContext,
                                TenantId = tenantId,
                                CompanyId = comID,
                                CreatedAt = DateTime.UtcNow,
                                LatencyMs = (int)totalDuration,
                                HandlerType = handlerType,
                                InputTokens = inputTokens,
                                OutputTokens = outputTokens,
                                GeminiCost = geminiCost,
                                ConfidenceScore = confidenceScore
                            };

                            await queryLogRepo.AddAsync(queryLog);
                            scopedLogger.LogDebug("[Orchestrator] Query log saved in bg: {LogId}", queryLogId);

                            scopedLogger.LogInformation("[Orchestrator] Triggering Auto-Capture for LogId: {LogId}, ComId: {ComId} in bg", queryLogId, comID);
                            var captureResult = await feedbackService.AutoCaptureExampleAsync(queryLogId, comID);
                            scopedLogger.LogInformation("[Orchestrator] Auto-Capture bg result: {Result}", captureResult);

                            // --- NEW CACHE STORAGE ---
                            if (_queryCacheEnabled && !string.IsNullOrWhiteSpace(rewrittenSql) && result.PageId.HasValue)
                            {
                                var queryCacheService = scope.ServiceProvider.GetRequiredService<IQueryCacheService>();
                                try
                                {
                                    await queryCacheService.StoreCachedSqlAsync(
                                        queryLogId,
                                        userQuestion,
                                        sharedVector,
                                        rewrittenSql,
                                        result.PageId ?? 0, // Safe fallback for strictly isolated general context
                                        tenantId,
                                        comID ?? 0,
                                        handlerType
                                    );
                                    scopedLogger.LogInformation("[Orchestrator] Saved to Global Query Cache in bg with Intent: {Intent}", handlerType);
                                }
                                catch (Exception cacheEx)
                                {
                                    scopedLogger.LogWarning(cacheEx, "[Orchestrator] Failed to save to Query Cache in bg");
                                }
                            }
                        }
                        catch (Exception logEx)
                        {
                            Console.WriteLine($"[Orchestrator] Failed to save query log in bg: {logEx.Message}");
                        }
                    });
                }
                else if (result.IsSuccess && result.IsCached)
                {
                    // 🆕 Save QueryLog for Cache HIT as well (no Auto-Capture, no Global Cache storage)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var queryLogRepo = scope.ServiceProvider.GetRequiredService<IQueryLogRepository>();
                            var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<QueryOrchestrator>>();

                            var queryLog = new Domain.Entities.QueryLog
                            {
                                Id = queryLogId,
                                RawQuestion = userQuestion,
                                GeneratedSql = result.RewrittenSql,
                                WasSuccessful = true,
                                SchemaContext = "Served from Global Query Cache",
                                TenantId = tenantId,
                                CompanyId = comID,
                                CreatedAt = DateTime.UtcNow,
                                LatencyMs = (int)totalDuration,
                                HandlerType = "CacheHIT",
                                InputTokens = 0,
                                OutputTokens = 0,
                                GeminiCost = 0m,
                                ConfidenceScore = result.ConfidenceScore
                            };

                            await queryLogRepo.AddAsync(queryLog);
                            scopedLogger.LogInformation("[Orchestrator] Cache-HIT Query log saved in bg: {LogId}", queryLogId);
                        }
                        catch (Exception logEx)
                        {
                            Console.WriteLine($"[Orchestrator] Failed to save Cache-HIT query log in bg: {logEx.Message}");
                        }
                    });
                }
                else if (!result.IsSuccess)
                {
                    _logger.LogWarning("[Orchestrator] Handler returned failure: {Error}", result.ErrorMessage);

                    if (result.ErrorMessage?.StartsWith("CLARIFICATION_REQUIRED:") == true)
                    {
                        var clarification = result.ErrorMessage.Replace("CLARIFICATION_REQUIRED:", "").Trim();

                        // 🛡️ Save clarification turn to session so the next followup answer (e.g. "نعم") has context.
                        // Without this, the Condenser won't know about the AI's question and will treat "نعم" as a standalone Chat query.
                        await _contextService.SaveTurnAsync(
                            sessionId,
                            clarification,                          // AI's clarification question becomes "previous question"
                            !string.IsNullOrEmpty(result.Intent) && result.Intent != "Unknown" ? result.Intent : (intentResult?.Intent.ToString() ?? "Unknown"),
                            result.RewrittenSql,                    // Preserve generated SQL for potential reuse
                            null,                                   // No result data yet
                            result.PageId                           // Keep PageId for context continuity
                        );
                        _logger.LogInformation("[Orchestrator] 📝 Clarification turn saved to session for followup context");

                        throw new InteractionRequiredException(clarification, queryLogId, result.ConfidenceScore ?? 0);
                    }
                }

                _logger.LogInformation("[Orchestrator] Pipeline Completed in {Duration:F0}ms", totalDuration);
                #region Context Saving

                if (result != null && result.IsSuccess)
                {
                    // 🚀 Save the turn asynchronously in background to avoid API blocking
                    var finalIntent = !string.IsNullOrEmpty(result.Intent) && result.Intent != "Unknown" ? result.Intent : (intentResult?.Intent.ToString() ?? "Unknown");
                    var rewrittenSql = result.RewrittenSql;
                    var data = result.Data;
                    var pageId = result.PageId;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var contextService = scope.ServiceProvider.GetRequiredService<IConversationContextService>();
                            await contextService.SaveTurnAsync(
                                sessionId,
                                userQuestion, 
                                finalIntent,
                                rewrittenSql,
                                data,
                                pageId
                            );
                        }
                        catch (Exception logEx)
                        {
                            Console.WriteLine($"[Orchestrator] Failed to save context in bg: {logEx.Message}");
                        }
                    });
                }

                #endregion

                if (result != null)
                {
                    if (string.IsNullOrEmpty(result.Intent) || result.Intent == "Unknown")
                    {
                        result.Intent = intentResult?.Intent.ToString() ?? "Unknown";
                    }
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

                // 🚀 Fire-and-forget error logging
                var errorLogHandlerType = (result != null && !string.IsNullOrEmpty(result.Intent) && result.Intent != "Unknown") ? result.Intent : (intentResult?.Intent.ToString() ?? "Unknown");
                var errorLogConfidenceScore = intentResult?.ConfidenceScore;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var queryLogRepo = scope.ServiceProvider.GetRequiredService<IQueryLogRepository>();
                        var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<QueryOrchestrator>>();

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
                            HandlerType = errorLogHandlerType,
                            ConfidenceScore = errorLogConfidenceScore
                        };

                        await queryLogRepo.AddAsync(errorLog);
                        scopedLogger.LogDebug("[Orchestrator] Error log saved in bg: {LogId}", queryLogId);
                    }
                    catch (Exception logSaveEx)
                    {
                        Console.WriteLine($"[Orchestrator] Failed to save error log in bg: {logSaveEx.Message}");
                    }
                });

                throw;
            }

            #endregion
        }

        public async Task<QueryExecutionResult> HandleDirectedPageQueryAsync(DirectedPageAskRequest request, CancellationToken ct = default)
        {
            #region Setup & Logging
            _logger.LogInformation("\n");
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║           DIRECTED PAGE INTEGRATION - PIPELINE START                   ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("[Orchestrator] Directed Question: \"{Question}\"", request.Question);
            _logger.LogInformation("[Orchestrator] Page: {PageName} (ID: {PageId})", request.ExplicitContext.PageName, request.PageId);

            var startTime = DateTime.UtcNow;
            Guid queryLogId = Guid.NewGuid();
            #endregion

            // 1. Conversation History Isolation (Explicitly passing PageId)
            _logger.LogInformation("[Orchestrator] Loading isolated context for PageId: {PageId}", request.PageId);
            var recentTurns = await _contextService.GetRecentContextAsync(request.SessionId ?? "direct-integration", 3, request.PageId, ct);

            // 2. Compute Embedding for Cache check
            var sharedEmbedding = await _embeddingService.GenerateAsync(request.Question, cancellationToken: ct);
            float[] sharedVector = sharedEmbedding.Vector.ToArray();

            // 3. Strict Cache Check (Isolated by PageId)
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

            // 4. Bypass classifies/RAG & Execute via Unified Handler
            if (!_handlerFactory.TryGetHandler(IntentType.DataRetrieval, out var handler))
            {
                throw new InvalidOperationException("Default DataRetrieval handler not found.");
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
                IsCondenserBypassed = true, // Force history usage without redacting
                PrecomputedEmbedding = sharedVector,
                ExplicitPageId = request.PageId,
                InjectedContext = request.ExplicitContext,
                
                // --- ALIGNMENT: Cache Fields ---
                CachedSql = cachedResult?.Sql,
                CachedPageId = cachedResult?.PageId,
                CachedIntent = cachedResult?.Intent,
                CachedCacheId = cachedResult?.CacheId,

                // Pass the connection string from the request body
                ConnectionString = request.ConnectionString
            };

            var intentStub = new IntentClassificationResult
            {
                // 🎯 Unknown = routing key only. UnifiedQueryHandler handles all intents.
                // The LLM freely decides the real intent (DataRetrieval/Chat/Prediction/PredictionWithDataRetrieval).
                Intent = IntentType.Unknown,
                ConfidenceScore = 1.0f,
                QueryEmbedding = sharedVector
            };

            QueryExecutionResult? result = null;

            try
            {
                result = await handler!.HandleAsync(request.Question, intentStub, context, ct);

                // 🔮 [AGENTIC 2-PASS] Delegate to shared helper (Gap 2 + Gap 3 aligned)
                if (result.IsSuccess && result.Intent == "PredictionWithDataRetrieval")
                {
                    await ExecuteAgenticPredictionPassAsync(
                        result,
                        request.Question,
                        request.PreviousQuestion,
                        request.SessionId ?? "direct-integration",
                        request.TenantId,
                        request.ComID,
                        queryLogId,
                        recentTurns,
                        condenserResult: null, // Condenser is bypassed in Directed flow — helper uses safe stub
                        ct);
                }

                // Gap 4: Handle failures, including CLARIFICATION_REQUIRED
                if (!result.IsSuccess)
                {
                    await HandleFailureResultAsync(
                        result,
                        request.SessionId ?? "direct-integration",
                        fallbackIntent: "DirectedIntegration",
                        queryLogId,
                        ct);
                }

                #region Post-Execution (Async Log & Cache)
                var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;

                if (result.IsSuccess && !result.IsCached)
                {
                    var inputTokens = (int)_usageTracker.GetSessionSummary().TotalInputTokens;
                    var outputTokens = (int)_usageTracker.GetSessionSummary().TotalOutputTokens;
                    var geminiCost = (decimal)_usageTracker.GetSessionSummary().TotalCostUsd;
                    var schemaContext = result.SchemaContext;
                    var rewrittenSql = result.RewrittenSql;
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

                            await queryLogRepo.AddAsync(new Domain.Entities.QueryLog
                            {
                                Id = queryLogId,
                                RawQuestion = request.Question,
                                GeneratedSql = rewrittenSql,
                                WasSuccessful = true,
                                SchemaContext = schemaContext,
                                TenantId = request.TenantId,
                                CompanyId = request.ComID,
                                CreatedAt = DateTime.UtcNow,
                                LatencyMs = (int)totalDuration,
                                HandlerType = finalHandlerType,
                                InputTokens = inputTokens,
                                OutputTokens = outputTokens,
                                GeminiCost = geminiCost,
                                ConfidenceScore = result.ConfidenceScore
                            });

                            await feedbackService.AutoCaptureExampleAsync(queryLogId, request.ComID);

                            if (_queryCacheEnabled && !string.IsNullOrWhiteSpace(rewrittenSql))
                            {
                                await queryCacheService.StoreCachedSqlAsync(
                                    queryLogId, request.Question, sharedVector,
                                    rewrittenSql, request.PageId,
                                    request.TenantId, request.ComID ?? 0, finalHandlerType);
                            }

                            await contextService.SaveTurnAsync(
                                request.SessionId ?? "direct-integration",
                                request.Question, finalHandlerType,
                                rewrittenSql, result.Data, request.PageId);
                        }
                        catch (Exception ex) { Console.WriteLine($"[DirectedOrchestrator] BG Error: {ex.Message}"); }
                    });
                }
                else if (result.IsSuccess && result.IsCached)
                {
                    var totalDurationCached = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var queryLogRepo = scope.ServiceProvider.GetRequiredService<IQueryLogRepository>();
                            await queryLogRepo.AddAsync(new Domain.Entities.QueryLog
                            {
                                Id = queryLogId,
                                RawQuestion = request.Question,
                                GeneratedSql = result.RewrittenSql,
                                WasSuccessful = true,
                                SchemaContext = "Served from Directed Cache",
                                TenantId = request.TenantId,
                                CompanyId = request.ComID,
                                CreatedAt = DateTime.UtcNow,
                                LatencyMs = (int)totalDurationCached,
                                HandlerType = "DirectedCacheHit",
                                InputTokens = 0,
                                OutputTokens = 0,
                                GeminiCost = 0m,
                                ConfidenceScore = 1.0f
                            });
                        }
                        catch { }
                    });
                }
                #endregion

                // Gap 5: Normalize result.Intent before returning.
                // UnifiedQueryHandler always stamps the real LLM-decided intent (Chat/Prediction/DataRetrieval).
                // Only falls back if the handler returned nothing (edge-case failure).
                if (string.IsNullOrEmpty(result.Intent) || result.Intent == "Unknown")
                    result.Intent = nameof(IntentType.DataRetrieval); // Safe fallback for directed context

                _logger.LogInformation("[Orchestrator] Directed Pipeline Completed in {Duration:F0}ms", (DateTime.UtcNow - startTime).TotalMilliseconds);
                return result;
            }
            catch (InteractionRequiredException)
            {
                throw; // Pass through to controller
            }
            catch (Exception ex)
            {
                // Gap 1: Structured error logging to DB via shared helper
                LogPipelineErrorAsync(
                    ex,
                    request.Question,
                    request.TenantId,
                    request.ComID,
                    queryLogId,
                    latencyMs: (DateTime.UtcNow - startTime).TotalMilliseconds,
                    handlerType: result?.Intent ?? "DirectedIntegration",
                    confidenceScore: null);
                throw;
            }
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
                    dataResult.PageId,
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
        /// [SOLID: SRP] Shared Agentic 2-Pass Pipeline.
        /// Triggered when a handler signals Intent == "PredictionWithDataRetrieval".
        /// Saves fetched data to context, resolves PredictIntentHandler, runs prediction,
        /// and merges the result into result.QueryInsights.Reasoning.
        /// </summary>
        private async Task ExecuteAgenticPredictionPassAsync(
            QueryExecutionResult result,
            string userQuestion,
            string? previousQuestion,
            string sessionId,
            string tenantId,
            int? comID,
            Guid queryLogId,
            List<ConversationTurn> recentTurns,
            CondenserResult? condenserResult,
            CancellationToken ct)
        {
            _logger.LogInformation("");
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║        [AGENTIC PASS] Triggering Prediction on Fetched Data            ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            // 1. Persist the fetched data immediately so PredictIntentHandler can read it
            await _contextService.SaveTurnAsync(sessionId, userQuestion, "DataRetrieval", result.RewrittenSql, result.Data, result.PageId, ct);
            _logger.LogInformation("[AgenticPass] ✓ Fetched data saved to context for prediction.");

            // 2. Resolve PredictIntentHandler explicitly (bypassing factory since UnifiedQueryHandler may shadow it)
            using var scope = _scopeFactory.CreateScope();
            var predictHandler = scope.ServiceProvider.GetServices<IIntentHandler>()
                                 .FirstOrDefault(h => h.GetType().Name == "PredictIntentHandler");

            if (predictHandler == null)
            {
                _logger.LogWarning("[AgenticPass] PredictIntentHandler not found — skipping prediction pass.");
                return;
            }

            var predictContext = new IntentHandlerContext
            {
                PreviousQuestion = previousQuestion,
                PreviousSql = result.RewrittenSql,
                QueryLogId = queryLogId,
                TenantId = tenantId,
                CompanyId = comID,
                SessionId = sessionId,
                ConversationHistory = recentTurns,
                // Condenser is bypassed in directed flow; pass a safe default stub
                PrecomputedCondenserResult = condenserResult ?? new CondenserResult
                {
                    ActionType = QueryActionType.New,
                    CondensedQuestion = userQuestion
                }
            };

            var mockIntent = new IntentClassificationResult { Intent = IntentType.Prediction, ConfidenceScore = 1.0f };
            var predictStart = DateTime.UtcNow;

            _logger.LogInformation("[AgenticPass] 🔮 Delegating to explicitly resolved PredictIntentHandler...");
            var predictionResult = await predictHandler.HandleAsync(userQuestion, mockIntent, predictContext, ct);

            _logger.LogInformation("[AgenticPass] ✓ Prediction generated in {Duration:F0}ms", (DateTime.UtcNow - predictStart).TotalMilliseconds);

            // 3. Merge prediction text into QueryInsights.Reasoning
            if (predictionResult.IsSuccess && predictionResult.Data != null)
            {
                var firstRow = predictionResult.Data.FirstOrDefault();
                if (firstRow != null)
                {
                    try
                    {
                        dynamic dynamicRow = firstRow;
                        string predictionText = dynamicRow.Prediction;
                        if (!string.IsNullOrEmpty(predictionText))
                        {
                            if (result.QueryInsights == null)
                                result.QueryInsights = new QueryInsightData { RetrievedContext = new List<string>() };
                            result.QueryInsights.Reasoning = predictionText;
                            _logger.LogInformation("[AgenticPass] ✓ Prediction merged into Reason field successfully.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("[AgenticPass] Failed to extract prediction text: {Msg}", ex.Message);
                    }
                }
            }
        }

        /// <summary>
        /// [SOLID: SRP] Handles a failed handler result.
        /// Detects CLARIFICATION_REQUIRED signal, saves the clarification turn to context
        /// so the next user follow-up has context, then throws InteractionRequiredException.
        /// </summary>
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

                // Save the AI's clarification question as a turn so the next answer ("yes"/"نعم") has context
                await _contextService.SaveTurnAsync(
                    sessionId,
                    clarification,
                    !string.IsNullOrEmpty(result.Intent) && result.Intent != "Unknown" ? result.Intent : fallbackIntent,
                    result.RewrittenSql,
                    null,
                    result.PageId,
                    ct);

                _logger.LogInformation("[Orchestrator] 📝 Clarification turn saved to session for followup context");
                throw new InteractionRequiredException(clarification, queryLogId, result.ConfidenceScore ?? 0);
            }
        }

        /// <summary>
        /// [SOLID: SRP] Fire-and-forget error logger.
        /// Saves a failed QueryLog entry to the DB without blocking the caller.
        /// </summary>
        private void LogPipelineErrorAsync(
            Exception ex,
            string userQuestion,
            string tenantId,
            int? comID,
            Guid queryLogId,
            double latencyMs,
            string handlerType,
            float? confidenceScore)
        {
            _logger.LogError(ex, "[Orchestrator] Pipeline Failed after {Duration:F0}ms", latencyMs);

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var queryLogRepo = scope.ServiceProvider.GetRequiredService<IQueryLogRepository>();
                    var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<QueryOrchestrator>>();

                    await queryLogRepo.AddAsync(new Domain.Entities.QueryLog
                    {
                        Id = queryLogId,
                        RawQuestion = userQuestion,
                        WasSuccessful = false,
                        ErrorMessage = ex.Message,
                        TenantId = tenantId,
                        CompanyId = comID,
                        CreatedAt = DateTime.UtcNow,
                        LatencyMs = (int)latencyMs,
                        HandlerType = handlerType,
                        ConfidenceScore = confidenceScore
                    });

                    scopedLogger.LogDebug("[Orchestrator] Error log saved in bg: {LogId}", queryLogId);
                }
                catch (Exception logSaveEx)
                {
                    Console.WriteLine($"[Orchestrator] Failed to save error log in bg: {logSaveEx.Message}");
                }
            });
        }


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
