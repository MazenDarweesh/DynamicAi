using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using DynamicAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicAi.Infrastructure.Services
{
    /// <summary>
    /// خدمة تتبع استخدام LLM — Provider-Agnostic.
    /// تخزن كل استدعاء في DB عبر IServiceScopeFactory (لأن الخدمة Singleton).
    /// تحسب التكاليف تلقائياً من الأسعار المكونة في appsettings.json.
    /// تحتفظ بملخص الجلسة In-Memory للتوافق مع QueryOrchestrator.
    /// </summary>
    public class LlmUsageTracker : ILlmUsageTracker
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<LlmUsageTracker> _logger;
        private readonly LlmProviderPricingConfig _pricingConfig;

        // In-Memory session tracking (backward compatible مع QueryOrchestrator)
        private readonly object _lock = new();
        private long _sessionInputTokens;
        private long _sessionOutputTokens;
        private long _sessionCachedTokens;
        private double _sessionCostUsd;
        private int _sessionCallCount;

        public LlmUsageTracker(
            IServiceScopeFactory scopeFactory,
            ILogger<LlmUsageTracker> logger,
            IOptions<LlmProviderPricingConfig> pricingConfig)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _pricingConfig = pricingConfig.Value;

            _logger.LogInformation(
                "🔧 LlmUsageTracker initialized — ActiveProvider: {Provider}, DailyBudget: ${Budget}",
                _pricingConfig.ActiveProvider, _pricingConfig.DailyBudgetUsd);
        }

        /// <inheritdoc/>
        public async Task TrackLlmCallAsync(
            LlmOperationType operationType,
            string requestId,
            int inputTokens,
            int outputTokens,
            int cachedTokens = 0,
            string? tenantId = null,
            int? companyId = null)
        {
            try
            {
                // Resolve the correct model name based on operation type
                string modelName = ResolveModelNameForOperation(operationType);
                decimal cost = 0m;

                // Calculate cost using config-driven pricing
                var pricing = _pricingConfig.GetActiveModelPricing(modelName);
                if (pricing != null)
                {
                    cost = pricing.CalculateCost(inputTokens, outputTokens, cachedTokens);
                }
                else
                {
                    _logger.LogWarning(
                        "⚠️ No pricing found for model '{Model}' under provider '{Provider}'. Cost will be $0.",
                        modelName, _pricingConfig.ActiveProvider);
                }

                // Save to DB
                await SaveUsageLogAsync(new LlmUsageLog
                {
                    RequestId = requestId,
                    OperationType = operationType,
                    ProviderName = _pricingConfig.ActiveProvider,
                    ModelName = modelName,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    CachedTokens = cachedTokens,
                    CostUsd = cost,
                    TenantId = tenantId,
                    CompanyId = companyId
                });

                // Update in-memory session
                lock (_lock)
                {
                    _sessionInputTokens += inputTokens;
                    _sessionOutputTokens += outputTokens;
                    _sessionCachedTokens += cachedTokens;
                    _sessionCostUsd += (double)cost;
                    _sessionCallCount++;
                }

                _logger.LogInformation(
                    "📊 LLM Usage: {Operation} | In:{Input} Out:{Output} Cache:{Cache} | Cost:${Cost:F6} | Provider:{Provider}/{Model}",
                    operationType, inputTokens, outputTokens, cachedTokens,
                    cost, _pricingConfig.ActiveProvider, modelName);
            }
            catch (Exception ex)
            {
                // Never let tracking failure break the main flow
                _logger.LogError(ex, "❌ Failed to track LLM usage for {Operation}", operationType);
            }
        }

        /// <inheritdoc/>
        public async Task TrackEmbeddingCallAsync(
            LlmOperationType operationType,
            string requestId,
            int tokenCount,
            string? tenantId = null,
            int? companyId = null)
        {
            try
            {
                string embeddingModel = ResolveEmbeddingModelName();
                decimal cost = 0m;

                var pricing = _pricingConfig.GetActiveModelPricing(embeddingModel);
                if (pricing != null)
                {
                    cost = pricing.CalculateCost(tokenCount, outputTokens: 0);
                }

                await SaveUsageLogAsync(new LlmUsageLog
                {
                    RequestId = requestId,
                    OperationType = operationType,
                    ProviderName = _pricingConfig.ActiveProvider,
                    ModelName = embeddingModel,
                    InputTokens = tokenCount,
                    OutputTokens = 0,
                    CachedTokens = 0,
                    CostUsd = cost,
                    TenantId = tenantId,
                    CompanyId = companyId
                });

                lock (_lock)
                {
                    _sessionInputTokens += tokenCount;
                    _sessionCostUsd += (double)cost;
                    _sessionCallCount++;
                }

                _logger.LogDebug(
                    "📊 Embedding Usage: {Operation} | Tokens:{Tokens} | Cost:${Cost:F6}",
                    operationType, tokenCount, cost);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to track embedding usage for {Operation}", operationType);
            }
        }

        /// <inheritdoc/>
        public async Task<decimal> GetDailyTotalCostAsync(DateTime date)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var startOfDay = date.Date;
                var endOfDay = startOfDay.AddDays(1);

                return await db.LlmUsageLogs
                    .Where(l => l.CreatedAt >= startOfDay && l.CreatedAt < endOfDay)
                    .SumAsync(l => l.CostUsd);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to get daily total cost for {Date}", date);
                return 0m;
            }
        }

        /// <inheritdoc/>
        public LlmSessionSummary GetSessionSummary()
        {
            lock (_lock)
            {
                return new LlmSessionSummary
                {
                    TotalInputTokens = _sessionInputTokens,
                    TotalOutputTokens = _sessionOutputTokens,
                    TotalCachedTokens = _sessionCachedTokens,
                    TotalCostUsd = _sessionCostUsd,
                    TotalCallCount = _sessionCallCount
                };
            }
        }

        /// <inheritdoc/>
        public void ResetSession()
        {
            lock (_lock)
            {
                _sessionInputTokens = 0;
                _sessionOutputTokens = 0;
                _sessionCachedTokens = 0;
                _sessionCostUsd = 0;
                _sessionCallCount = 0;
            }
        }

        #region Private Helpers

        /// <summary>
        /// يحفظ سجل الاستخدام في قاعدة البيانات عبر Scoped DbContext.
        /// </summary>
        private async Task SaveUsageLogAsync(LlmUsageLog log)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.LlmUsageLogs.Add(log);
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// يحدد اسم الموديل المناسب حسب نوع العملية.
        /// Embedding operations → embedding model, كل شيء آخر → chat model.
        /// </summary>
        private string ResolveModelNameForOperation(LlmOperationType operationType)
        {
            // Embedding operations get routed to the embedding model
            if (operationType == LlmOperationType.Embedding)
                return ResolveEmbeddingModelName();

            // All other operations (SQL, Chat, Prediction, etc.) → chat model
            return ResolveChatModelName();
        }

        /// <summary>
        /// يحدد اسم موديل المحادثة/التوليد من تكوين المزود.
        /// يختار أول موديل ليس embedding من قائمة الموديلات.
        /// </summary>
        private string ResolveChatModelName()
        {
            var provider = _pricingConfig.GetActiveProviderConfig();
            if (provider?.Models == null || !provider.Models.Any())
                return "unknown";

            return provider.Models.Keys
                .FirstOrDefault(k => !k.Contains("embedding", StringComparison.OrdinalIgnoreCase))
                ?? provider.Models.Keys.First();
        }

        /// <summary>
        /// يحدد اسم موديل التضمين من تكوين المزود.
        /// يختار أول موديل يحتوي على "embedding" في اسمه.
        /// </summary>
        private string ResolveEmbeddingModelName()
        {
            var provider = _pricingConfig.GetActiveProviderConfig();
            if (provider?.Models == null || !provider.Models.Any())
                return "unknown";

            return provider.Models.Keys
                .FirstOrDefault(k => k.Contains("embedding", StringComparison.OrdinalIgnoreCase))
                ?? provider.Models.Keys.First();
        }

        #endregion
    }
}
