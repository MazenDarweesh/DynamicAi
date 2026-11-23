using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Domain.Entities;
using DynamicAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicAi.Infrastructure.Services
{
    /// <summary>
    /// خدمة تتبع استخدام LLM — Provider-Agnostic.
    /// تخزن كل استدعاء في DB عبر IServiceScopeFactory (لأن الخدمة Singleton).
    /// تحسب التكاليف تلقائياً من الأسعار المكونة في appsettings.json.
    /// تحتفظ بملخص الجلسة In-Memory وتخزن إجمالي التكلفة اليومية مؤقتاً لمدة 60 ثانية.
    /// </summary>
    public class LlmUsageTracker : ILlmUsageTracker
    {
        private static readonly TimeSpan DailyCostCacheTtl = TimeSpan.FromSeconds(60);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<LlmUsageTracker> _logger;
        private readonly LlmProviderPricingConfig _pricingConfig;
        private readonly IConfiguration _configuration;
        private readonly IMemoryCache _memoryCache;

        private readonly SemaphoreSlim _dailyCacheLock = new(1, 1);

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
            IOptions<LlmProviderPricingConfig> pricingConfig,
            IConfiguration configuration,
            IMemoryCache memoryCache)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pricingConfig = pricingConfig?.Value ?? throw new ArgumentNullException(nameof(pricingConfig));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));

            _logger.LogInformation(
                "🔧 LlmUsageTracker initialized — ActiveProvider: {Provider}, DailyBudget: ${Budget}",
                _pricingConfig.ActiveProvider, _pricingConfig.DailyBudgetUsd);
        }

        /// <inheritdoc/>
        public async Task<decimal> TrackLlmCallAsync(
            LlmOperationType operationType,
            string requestId,
            int inputTokens,
            int outputTokens,
            int cachedTokens = 0,
            string? tenantId = null,
            int? companyId = null,
            string? modelName = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string resolvedModel = !string.IsNullOrWhiteSpace(modelName)
                    ? modelName.Trim()
                    : ResolveModelNameForOperation(operationType);

                decimal cost = 0m;
                var pricing = _pricingConfig.GetActiveModelPricing(resolvedModel);
                if (pricing != null)
                {
                    cost = pricing.CalculateCost(inputTokens, outputTokens, cachedTokens);
                }
                else
                {
                    _logger.LogWarning(
                        "⚠️ No pricing found for model '{Model}' under provider '{Provider}'. Cost will be $0.",
                        resolvedModel, _pricingConfig.ActiveProvider);
                }

                await SaveUsageLogAsync(new LlmUsageLog
                {
                    RequestId = requestId,
                    OperationType = operationType,
                    ProviderName = _pricingConfig.ActiveProvider,
                    ModelName = resolvedModel,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    CachedTokens = cachedTokens,
                    CostUsd = cost,
                    TenantId = tenantId,
                    CompanyId = companyId
                }, cancellationToken);

                lock (_lock)
                {
                    _sessionInputTokens += inputTokens;
                    _sessionOutputTokens += outputTokens;
                    _sessionCachedTokens += cachedTokens;
                    _sessionCostUsd += (double)cost;
                    _sessionCallCount++;
                }

                await IncrementCachedDailyTotalAsync(DateTime.UtcNow.Date, cost, cancellationToken);

                _logger.LogInformation(
                    "📊 LLM Usage: {Operation} | In:{Input} Out:{Output} Cache:{Cache} | Cost:${Cost:F6} | Provider:{Provider}/{Model}",
                    operationType, inputTokens, outputTokens, cachedTokens,
                    cost, _pricingConfig.ActiveProvider, resolvedModel);

                return cost;
            }
            catch (Exception ex)
            {
                // Never let tracking failure break the main flow
                _logger.LogError(ex, "❌ Failed to track LLM usage for {Operation}", operationType);
                return 0m;
            }
        }

        /// <inheritdoc/>
        public async Task<decimal> TrackEmbeddingCallAsync(
            LlmOperationType operationType,
            string requestId,
            int tokenCount,
            string? tenantId = null,
            int? companyId = null,
            string? modelName = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string embeddingModel = !string.IsNullOrWhiteSpace(modelName)
                    ? modelName.Trim()
                    : ResolveEmbeddingModelName();

                decimal cost = 0m;
                var pricing = _pricingConfig.GetActiveModelPricing(embeddingModel);
                if (pricing != null)
                {
                    cost = pricing.CalculateCost(tokenCount, outputTokens: 0);
                }
                else
                {
                    _logger.LogWarning(
                        "⚠️ No pricing found for embedding model '{Model}' under provider '{Provider}'. Cost will be $0.",
                        embeddingModel, _pricingConfig.ActiveProvider);
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
                }, cancellationToken);

                lock (_lock)
                {
                    _sessionInputTokens += tokenCount;
                    _sessionCostUsd += (double)cost;
                    _sessionCallCount++;
                }

                await IncrementCachedDailyTotalAsync(DateTime.UtcNow.Date, cost, cancellationToken);

                _logger.LogDebug(
                    "📊 Embedding Usage: {Operation} | Tokens:{Tokens} | Cost:${Cost:F6} | Model:{Model}",
                    operationType, tokenCount, cost, embeddingModel);

                return cost;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to track embedding usage for {Operation}", operationType);
                return 0m;
            }
        }

        /// <inheritdoc/>
        public async Task<decimal> GetDailyTotalCostAsync(DateTime date)
        {
            var targetDate = date.Kind == DateTimeKind.Local
                ? date.ToUniversalTime().Date
                : date.Date;
            var cacheKey = GetDailyCostCacheKey(targetDate);

            if (_memoryCache.TryGetValue(cacheKey, out decimal cachedTotal))
            {
                return cachedTotal;
            }

            await _dailyCacheLock.WaitAsync();
            try
            {
                if (_memoryCache.TryGetValue(cacheKey, out cachedTotal))
                {
                    return cachedTotal;
                }

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var startOfDay = targetDate;
                var endOfDay = startOfDay.AddDays(1);

                var total = await db.LlmUsageLogs
                    .Where(l => l.CreatedAt >= startOfDay && l.CreatedAt < endOfDay)
                    .SumAsync(l => l.CostUsd);

                _memoryCache.Set(cacheKey, total, DailyCostCacheTtl);
                return total;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to get daily total cost for {Date}", targetDate);
                return 0m;
            }
            finally
            {
                _dailyCacheLock.Release();
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

        private static string GetDailyCostCacheKey(DateTime utcDate) =>
            $"llm_daily_cost_{utcDate:yyyy-MM-dd}";

        private async Task IncrementCachedDailyTotalAsync(DateTime utcDate, decimal addedCost, CancellationToken cancellationToken)
        {
            if (addedCost <= 0m)
            {
                return;
            }

            var cacheKey = GetDailyCostCacheKey(utcDate);
            await _dailyCacheLock.WaitAsync(cancellationToken);
            try
            {
                if (_memoryCache.TryGetValue(cacheKey, out decimal currentTotal))
                {
                    _memoryCache.Set(cacheKey, currentTotal + addedCost, DailyCostCacheTtl);
                }
            }
            finally
            {
                _dailyCacheLock.Release();
            }
        }

        /// <summary>
        /// يحفظ سجل الاستخدام في قاعدة البيانات عبر Scoped DbContext.
        /// </summary>
        private async Task SaveUsageLogAsync(LlmUsageLog log, CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.LlmUsageLogs.Add(log);
            await db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// يحدد اسم الموديل المناسب حسب نوع العملية.
        /// </summary>
        private string ResolveModelNameForOperation(LlmOperationType operationType)
        {
            return operationType switch
            {
                LlmOperationType.Embedding => ResolveEmbeddingModelName(),
                LlmOperationType.Prediction => ResolvePredictModelName(),
                _ => ResolveChatModelName()
            };
        }

        /// <summary>
        /// يحدد اسم موديل المحادثة/التوليد من IConfiguration (Gemini:ChatModel) مع fallback لتكوين المزود.
        /// </summary>
        private string ResolveChatModelName()
        {
            var configured = _configuration["Gemini:ChatModel"];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            var provider = _pricingConfig.GetActiveProviderConfig();
            if (provider?.Models == null || !provider.Models.Any())
                return "unknown";

            return provider.Models.Keys
                .FirstOrDefault(k => !k.Contains("embedding", StringComparison.OrdinalIgnoreCase))
                ?? provider.Models.Keys.First();
        }

        /// <summary>
        /// يحدد اسم موديل التنبؤ من IConfiguration (Gemini:PredictModel) مع fallback لموديل المحادثة.
        /// </summary>
        private string ResolvePredictModelName()
        {
            var configured = _configuration["Gemini:PredictModel"];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            return ResolveChatModelName();
        }

        /// <summary>
        /// يحدد اسم موديل التضمين من IConfiguration (Gemini:EmbeddingModel) مع fallback لتكوين المزود.
        /// </summary>
        private string ResolveEmbeddingModelName()
        {
            var configured = _configuration["Gemini:EmbeddingModel"];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

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
