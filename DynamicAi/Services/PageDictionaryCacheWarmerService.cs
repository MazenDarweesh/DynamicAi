using Dapper;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicAi.Api.Services
{
    /// <summary>
    /// خدمة خلفية مزدوجة الغرض تعمل عند بدء التشغيل وكل 60 دقيقة:
    ///
    /// 1. تبني قائمة الصفحات الخفيفة (PageDictionaryItem) → للـ Frontend Dropdown
    ///    وتحفظها عبر IPageDictionaryProvider.UpdateCache()
    ///
    /// 2. تسخّن (Pre-warm) الـ RAG Context Cache لكل صفحة مفعّلة (AISetup=1)
    ///    بنفس مفتاح الكاش الذي يستخدمه QdrantRetrievalService:
    ///    "RagContext:{tenantId}:{pageId}"
    ///    → عندما يصل طلب مع pageId محدد، يجد QdrantRetrievalService
    ///      كاش جاهز في سطر 248 → يرجعه فوراً (صفر Qdrant calls!)
    /// </summary>
    public class PageDictionaryCacheWarmerService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IPageDictionaryProvider _pageDictionaryProvider;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<PageDictionaryCacheWarmerService> _logger;
        private readonly string _connectionString;
        private readonly string _defaultTenantId;
        private readonly int _ragCacheTTLMinutes;

        private readonly TimeSpan _refreshInterval     = TimeSpan.FromMinutes(60);
        private readonly TimeSpan _failureRetryInterval = TimeSpan.FromSeconds(30);
        private const int DB_COMMAND_TIMEOUT_SECONDS   = 15;
        private const string RAG_CACHE_KEY_PREFIX      = "RagContext";

        public PageDictionaryCacheWarmerService(
            IServiceProvider serviceProvider,
            IPageDictionaryProvider pageDictionaryProvider,
            IMemoryCache memoryCache,
            IConfiguration configuration,
            ILogger<PageDictionaryCacheWarmerService> logger)
        {
            _serviceProvider         = serviceProvider;
            _pageDictionaryProvider  = pageDictionaryProvider;
            _memoryCache             = memoryCache;
            _logger                  = logger;
            _connectionString        = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("[PageDictionaryWarmer] 'DefaultConnection' is not configured.");
            _defaultTenantId         = configuration["Application:DefaultTenantId"] ?? "ALL";
            _ragCacheTTLMinutes      = int.TryParse(configuration["Caching:RagContextCache:TTLMinutes"], out var ttl) ? ttl : 240;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[PageDictionaryWarmer] 🚀 Service starting...");

            // انتظر 5 ثواني لضمان جهوزية الخدمات الأخرى (Qdrant، إلخ)
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            var success = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                success = false;
                try
                {
                    await WarmCacheAsync(stoppingToken);
                    success = true;
                }
                catch (SqlException ex)
                {
                    _logger.LogError(ex, "[PageDictionaryWarmer] 🔴 SQL Error. Will retry in {Seconds}s.", _failureRetryInterval.TotalSeconds);
                }
                catch (TaskCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[PageDictionaryWarmer] ⚠️ Unexpected error. Will retry in {Seconds}s.", _failureRetryInterval.TotalSeconds);
                }

                var waitTime = success ? _refreshInterval : _failureRetryInterval;
                await Task.Delay(waitTime, stoppingToken);
            }

            _logger.LogInformation("[PageDictionaryWarmer] Service stopping.");
        }

        // ============================================================================
        // MAIN WARM LOGIC
        // ============================================================================
        private async Task WarmCacheAsync(CancellationToken ct)
        {
            var warmStart = DateTime.UtcNow;
            _logger.LogInformation("[PageDictionaryWarmer] ══════════ Starting Cache Warm-up ══════════");

            // ─────────────────────────────────────
            // STEP A: جلب قائمة الصفحات من SQL Server
            // ─────────────────────────────────────
            var pages = await FetchPageListAsync(ct);
            if (!pages.Any())
            {
                _logger.LogWarning("[PageDictionaryWarmer] ⚠️ No AI-enabled pages found. Skipping warm-up.");
                return;
            }

            // تحديث الـ Frontend Dictionary (النسخة الخفيفة)
            _pageDictionaryProvider.UpdateCache(pages);
            _logger.LogInformation("[PageDictionaryWarmer] ✅ STEP A: Page list cached. Count={Count}", pages.Count);

            // ─────────────────────────────────────
            // STEP B: Pre-warm RAG Context Cache لكل صفحة
            // ─────────────────────────────────────
            _logger.LogInformation("[PageDictionaryWarmer] 🔥 STEP B: Pre-warming RAG Context Cache for {Count} pages...", pages.Count);

            using var scope = _serviceProvider.CreateScope();
            var ragService  = scope.ServiceProvider.GetRequiredService<IRagRetrievalService>();

            var warmedCount = 0;
            var failedCount = 0;

            foreach (var page in pages)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var cacheKey = $"{RAG_CACHE_KEY_PREFIX}:{_defaultTenantId}:{page.PageId}";

                    // تخطي إذا كان الكاش موجوداً وحديثاً
                    if (_memoryCache.TryGetValue(cacheKey, out _))
                    {
                        _logger.LogDebug("[PageDictionaryWarmer] ⏭ PageID={PageId} already cached. Skipping.", page.PageId);
                        warmedCount++;
                        continue;
                    }

                    // بناء الـ Full Context من Qdrant
                    _logger.LogDebug("[PageDictionaryWarmer] 🔍 Fetching context for PageID={PageId} '{PageName}'...", page.PageId, page.PageName);
                    var pageStart = DateTime.UtcNow;

                    var fullContext = await ragService.GetFullPageContextForLlmCachingAsync(
                        pageId:    page.PageId,
                        tenantId:  _defaultTenantId,
                        ct:        ct);

                    var fetchMs = (DateTime.UtcNow - pageStart).TotalMilliseconds;

                    // تحويل FullPageContextForLlmCache → CachedPageContext وحفظه
                    var cachedContext = new CachedPageContext
                    {
                        PageId             = page.PageId,
                        UnifiedPageContext = fullContext.MinimalSchemaContext ?? string.Empty,
                        DictionaryEntries  = fullContext.AllDictionaryEntries ?? new List<string>(),
                        BusinessRules      = fullContext.BusinessRules ?? new List<string>(),
                        CachedAt           = DateTime.UtcNow
                    };

                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(TimeSpan.FromMinutes(_ragCacheTTLMinutes))
                        .SetSlidingExpiration(TimeSpan.FromMinutes(_ragCacheTTLMinutes / 2));

                    _memoryCache.Set(cacheKey, cachedContext, cacheOptions);

                    warmedCount++;
                    _logger.LogInformation(
                        "[PageDictionaryWarmer] ✅ PageID={PageId} '{PageName}' warmed in {FetchMs:F0}ms. " +
                        "Context: {ContextSize} chars, {DictCount} dict entries, {RulesCount} rules.",
                        page.PageId, page.PageName, fetchMs,
                        cachedContext.UnifiedPageContext.Length,
                        cachedContext.DictionaryEntries.Count,
                        cachedContext.BusinessRules.Count);

                    // استراحة قصيرة بين كل صفحة لعدم إغراق Qdrant
                    await Task.Delay(200, ct);
                }
                catch (Exception ex)
                {
                    failedCount++;
                    _logger.LogWarning(ex, "[PageDictionaryWarmer] ⚠️ Failed to warm PageID={PageId}. Continuing...", page.PageId);
                }
            }

            var totalMs = (DateTime.UtcNow - warmStart).TotalMilliseconds;
            _logger.LogInformation(
                "[PageDictionaryWarmer] ══════════ Warm-up COMPLETE ══════════\n" +
                "  ✅ Warmed:  {Warmed}\n  ❌ Failed:  {Failed}\n  ⏱ Total:   {TotalMs:F0}ms\n" +
                "  🔄 Next refresh in {NextMinutes} minutes.",
                warmedCount, failedCount, totalMs, _refreshInterval.TotalMinutes);
        }

        // ============================================================================
        // SQL: جلب قائمة الصفحات من SQL Server
        // ============================================================================
        private async Task<IReadOnlyList<PageDictionaryItem>> FetchPageListAsync(CancellationToken ct)
        {
            const string sql = @"
                SELECT
                    p.PageID        AS PageId,
                    p.PageDisplay   AS PageName,
                    ISNULL(m.MEnName,  'General')   AS ModuleName,
                    ISNULL(mm.MEnName, 'General')   AS MenuName,
                    ISNULL(m.MEnName,  'General') + ' - ' +
                    ISNULL(mm.MEnName, 'General')   AS GroupName
                FROM Pages p
                LEFT JOIN Modul      m  ON p.ModulID  = m.MID
                LEFT JOIN MenuMaster mm ON p.MasterID = mm.MID
                WHERE p.AISetup = 1
                  AND p.PageID  IS NOT NULL
                ORDER BY m.MEnName, mm.MEnName, p.PageDisplay";

            await using var connection = new SqlConnection(_connectionString);
            var result = await connection.QueryAsync<PageDictionaryItem>(
                new CommandDefinition(sql, commandTimeout: DB_COMMAND_TIMEOUT_SECONDS, cancellationToken: ct));

            return result.ToList();
        }
    }
}
