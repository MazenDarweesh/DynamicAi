using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// التنفيذ الفعلي لـ IQueryOrchestrator.
    /// يقوم بربط جميع الخدمات (Layers 1-5) معاً بالترتيب الصحيح.
    /// </summary>
    public class QueryOrchestrator : IQueryOrchestrator
    {
        private readonly IRagRetrievalService _retrievalService;         // (Layer 1)
        private readonly ISqlGenerationService _generationService;       // (Layer 2)
        private readonly ISqlValidatorService _validatorService;         // (Layer 3)
        private readonly ISqlRewriterService _rewriterService;           // (Layer 4)
        private readonly ISandboxedSqlExecutor _executorService;         // (Layer 5)
        private readonly IQueryCacheService _queryCacheService;          // (Cache Layer)
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
        private readonly ILogger<QueryOrchestrator> _logger;
        private readonly bool _queryCacheEnabled;

        public QueryOrchestrator(
            IRagRetrievalService retrievalService,
            ISqlGenerationService generationService,
            ISqlValidatorService validatorService,
            ISqlRewriterService rewriterService,
            ISandboxedSqlExecutor executorService,
            IQueryCacheService queryCacheService,
            IEmbeddingGenerator<string, Embedding<float>> embeddingService,
            ILogger<QueryOrchestrator> logger,
            IConfiguration configuration)
        {
            _retrievalService = retrievalService;
            _generationService = generationService;
            _validatorService = validatorService;
            _rewriterService = rewriterService;
            _executorService = executorService;
            _queryCacheService = queryCacheService;
            _embeddingService = embeddingService;
            _logger = logger;
            _queryCacheEnabled = bool.Parse(configuration["Caching:QueryCache:Enabled"] ?? "true");
        }

        /// <summary>
        /// يعالج سؤال المستخدم بالكامل، مروراً بجميع طبقات الأمان والتوليد.
        /// </summary>
        /// <summary>
        /// يعالج سؤال المستخدم بالكامل، مروراً بجميع طبقات الأمان والتوليد.
        /// </summary>
        /// <summary>
        /// يعالج سؤال المستخدم بالكامل، مروراً بجميع طبقات الأمان والتوليد.
        /// </summary>
        public async Task<QueryExecutionResult> HandleQueryAsync(string userQuestion, string tenantId, int? comID = null)
        {
            // ============================================================================
            // بداية Pipeline - عرض معلومات الطلب
            // ============================================================================
            _logger.LogInformation("\n");
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║                  QUERY ORCHESTRATOR - PIPELINE START                   ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            _logger.LogInformation("[Orchestrator] User Question: \"{UserQuestion}\"", userQuestion);
            _logger.LogInformation("[Orchestrator] Tenant ID: {TenantId}", tenantId);
            _logger.LogInformation("[Orchestrator] Company ID: {CompanyId}", comID.HasValue ? comID.Value.ToString() : "null");
            _logger.LogInformation("[Orchestrator] Pipeline has 5 layers to execute...");

            var startTime = DateTime.UtcNow;
            string rawSql = string.Empty;
            RewrittenSqlResult? rewrittenResult = null;
            float[]? questionEmbedding = null;
            int? pageId = null;
            string? cacheId = null;
            bool isCached = false;
            double layer1Duration = 0, layer2Duration = 0; // Initialized for cache HIT path

            try
            {
                // ============================================================================
                // Layer 0: Query Cache Check - البحث عن نتيجة مخزنة
                // ============================================================================
                if (_queryCacheEnabled)
                {
                    _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
                    _logger.LogInformation("║               [Layer 0: QUERY CACHE] - CHECKING                        ║");
                    _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

                    var cacheCheckStart = DateTime.UtcNow;
                    var embedding = await _embeddingService.GenerateAsync(userQuestion);
                    questionEmbedding = embedding.Vector.ToArray();

                    var cachedResult = await _queryCacheService.TryGetCachedSqlAsync(
                        questionEmbedding, tenantId, comID ?? 0);

                    var cacheCheckDuration = (DateTime.UtcNow - cacheCheckStart).TotalMilliseconds;

                    if (cachedResult != null)
                    {
                        _logger.LogInformation("[Orchestrator] ✓ Cache HIT in {Duration:F0}ms - Skipping Layers 1-2!", cacheCheckDuration);

                        rawSql = cachedResult.Sql;
                        cacheId = cachedResult.CacheId;
                        isCached = true;
                        // Skip to Layer 3 (validation)
                        goto SkipToValidation;
                    }

                    _logger.LogInformation("[Orchestrator] Cache checked in {Duration:F0}ms - proceeding to full pipeline", cacheCheckDuration);
                }

                // ============================================================================
                // Layer 1: RAG - استرجاع السياق من قاعدة المعرفة
                // ============================================================================
                _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
                _logger.LogInformation("║                [Layer 1: RAG RETRIEVAL] - STARTING                     ║");
                _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

                var layer1Start = DateTime.UtcNow;
                var contextSnippets = await _retrievalService.RetrieveContextAsync(userQuestion, tenantId);
                layer1Duration = (DateTime.UtcNow - layer1Start).TotalMilliseconds;

                _logger.LogInformation("[Orchestrator] ✓ Layer 1 completed in {Duration:F0}ms", layer1Duration);
                _logger.LogInformation("[Orchestrator] Retrieved context bundle with Unified Context: {UnifiedContext}, {DictCount} dictionary entry(ies), and {RuleCount} business rule(s).",
                     !string.IsNullOrEmpty(contextSnippets.UnifiedPageContext),
                     contextSnippets?.DictionaryEntries?.Count ?? 0,
                     contextSnippets?.BusinessRules?.Count ?? 0);

                // Extract PageId for cache storage
                pageId = ExtractPageIdFromContext(contextSnippets?.UnifiedPageContext);

                // ============================================================================
                // Layer 2: SQL Generation - توليد SQL من LLM
                // ============================================================================
                _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
                _logger.LogInformation("║              [Layer 2: SQL GENERATION] - STARTING                      ║");
                _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

                var layer2Start = DateTime.UtcNow;
                rawSql = await _generationService.GenerateSqlAsync(userQuestion, contextSnippets);
                layer2Duration = (DateTime.UtcNow - layer2Start).TotalMilliseconds;

                _logger.LogInformation("[Orchestrator] ✓ Layer 2 completed in {Duration:F0}ms", layer2Duration);

                // ============================================================================
                // Interactivity Check: Handle CLARIFICATION_NEEDED
                // ============================================================================
                if (rawSql.Trim().StartsWith("CLARIFICATION_NEEDED:", StringComparison.OrdinalIgnoreCase))
                {
                    var clarificationMessage = rawSql.Replace("CLARIFICATION_NEEDED:", "", StringComparison.OrdinalIgnoreCase).Trim();

                    _logger.LogWarning("[Orchestrator] ✋ Interactivity Triggered: AI requesting clarification from user.");
                    _logger.LogInformation("[Orchestrator] Clarification Message: \"{ClarificationMessage}\"", clarificationMessage);

                    // Throw a specific exception that the Controller will catch and handle
                    throw new InvalidOperationException($"INTERACTION_REQUIRED: {clarificationMessage}");
                }

            SkipToValidation:
                // ============================================================================
                // Layer 3: SQL Validation - التحقق من الأمان
                // ============================================================================
                var layer3Start = DateTime.UtcNow;
                await _validatorService.ValidateSqlAsync(rawSql, tenantId);
                var layer3Duration = (DateTime.UtcNow - layer3Start).TotalMilliseconds;

                _logger.LogInformation("[Orchestrator] ✓ Layer 3 completed in {Duration:F0}ms", layer3Duration);

                // Store in cache AFTER validation passes (never cache invalid SQL!)
                if (_queryCacheEnabled && questionEmbedding != null && !isCached)
                {
                    await _queryCacheService.StoreCachedSqlAsync(
                        userQuestion, questionEmbedding, rawSql,
                        pageId ?? 0, tenantId, comID ?? 0);
                }

                // ============================================================================
                // Layer 4: SQL Rewriting - (مُفعّلة الآن)
                // ============================================================================
                _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
                _logger.LogInformation("║               [Layer 4: SQL REWRITER] - STARTING                       ║");
                _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

                var layer4Start = DateTime.UtcNow;
                rewrittenResult = await _rewriterService.RewriteAsync(rawSql, tenantId, comID);
                var layer4Duration = (DateTime.UtcNow - layer4Start).TotalMilliseconds;

                _logger.LogInformation("[Orchestrator] ✓ Layer 4 completed in {Duration:F0}ms", layer4Duration);
                _logger.LogInformation("[Orchestrator] Hardened SQL:\n{RewrittenSql}", rewrittenResult.RewrittenSql);
                _logger.LogDebug("[Orchestrator] Parameters: {ParamCount}", rewrittenResult.Parameters.Count);
                foreach (var param in rewrittenResult.Parameters)
                {
                    _logger.LogDebug("  - {Key} = {Value}", param.Key, param.Value);
                }

                // ============================================================================
                // Layer 5: SQL Execution - التنفيذ الآمن
                // ============================================================================
                _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
                _logger.LogInformation("║            [Layer 5: SANDBOXED EXECUTOR] - STARTING                    ║");
                _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

                var layer5Start = DateTime.UtcNow;
                var data = await _executorService.ExecuteSqlAsync(rewrittenResult.RewrittenSql, rewrittenResult.Parameters);
                var layer5Duration = (DateTime.UtcNow - layer5Start).TotalMilliseconds;

                _logger.LogInformation("[Orchestrator] ✓ Layer 5 completed in {Duration:F0}ms", layer5Duration);
                _logger.LogInformation("[Orchestrator] Query executed successfully. Rows returned.");

                // ============================================================================
                // Pipeline مكتمل - عرض الإحصائيات النهائية
                // ============================================================================
                var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;

                _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
                _logger.LogInformation("║              QUERY ORCHESTRATOR - PIPELINE COMPLETED ✓                 ║");
                _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

                _logger.LogInformation("\n[Orchestrator] ═══ PIPELINE PERFORMANCE SUMMARY ═══");
                _logger.LogInformation("  Layer 1 (RAG Retrieval):      {Layer1Duration,8:F0} ms", layer1Duration);
                _logger.LogInformation("  Layer 2 (SQL Generation):     {Layer2Duration,8:F0} ms", layer2Duration);
                _logger.LogInformation("  Layer 3 (SQL Validation):     {Layer3Duration,8:F0} ms", layer3Duration);
                _logger.LogInformation("  Layer 4 (SQL Rewriter):       {Layer4Duration,8:F0} ms", layer4Duration);
                _logger.LogInformation("  Layer 5 (Execution):          {Layer5Duration,8:F0} ms", layer5Duration);
                _logger.LogInformation("  ───────────────────────────────────────────────");
                _logger.LogInformation("  Total Pipeline Duration:      {TotalDuration,8:F0} ms", totalDuration);
                _logger.LogInformation("[Orchestrator] ══════════════════════════════════════");

                return new QueryExecutionResult
                {
                    Data = data,
                    RewrittenSql = rewrittenResult?.RewrittenSql ?? rawSql,
                    IsCached = isCached,
                    CacheId = cacheId
                };
            }
            catch (Exception ex)
            {
                var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;

                _logger.LogError("\n╔════════════════════════════════════════════════════════════════════════╗");
                _logger.LogError("║              QUERY ORCHESTRATOR - PIPELINE FAILED ✗                    ║");
                _logger.LogError("╚════════════════════════════════════════════════════════════════════════╝");

                _logger.LogError("[Orchestrator] Error Type: {ErrorType}", ex.GetType().Name);
                _logger.LogError("[Orchestrator] Error Message: {ErrorMessage}", ex.Message);
                _logger.LogError("[Orchestrator] Failed after {Duration:F0}ms", totalDuration);

                if (ex.StackTrace != null)
                {
                    _logger.LogError("[Orchestrator] Stack Trace:\n{StackTrace}", ex.StackTrace);
                }

                // If we have generated SQL, wrap the exception to pass it up
                if (!string.IsNullOrEmpty(rewrittenResult?.RewrittenSql ?? rawSql))
                {
                    _logger.LogWarning("[Orchestrator] Wrapping {ErrorType} in QueryExecutionException to preserve SQL.", ex.GetType().Name);

                    throw new QueryExecutionException(
                        ex.Message,
                        rewrittenResult?.RewrittenSql ?? rawSql,
                        ex);
                }

                throw;
            }
        }

        /// <summary>
        /// Extracts PageId from the UnifiedPageContext JSON.
        /// </summary>
        private int? ExtractPageIdFromContext(string? unifiedContext)
        {
            if (string.IsNullOrEmpty(unifiedContext)) return null;

            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    unifiedContext, @"""PageID""\s*:\s*(\d+)");
                if (match.Success)
                {
                    return int.Parse(match.Groups[1].Value);
                }
            }
            catch
            {
                // Ignore parsing errors
            }
            return null;
        }
    }
}
