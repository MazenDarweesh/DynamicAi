using Dapper;
using DynamicAi.Application.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicAi.Application.Services.SqlEngine
{
    /// <summary>
    /// (Layer 2.7 Helper)
    /// يوفر هيكل قاعدة البيانات (الجداول والأعمدة) من Memory Cache أو Disk Fallback.
    /// 
    /// يدعم مسارين:
    ///   1. Default Path   → GetSchemaMapAsync()       → الـ Global Cache (DefaultConnection, warmed at startup)
    ///   2. Per-Request    → GetSchemaForViewsAsync()  → Lazy, targeted fetch من connection مخصص (ConcurrentDictionary)
    /// </summary>
    public class DbSchemaProvider : IDbSchemaProvider
    {
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<DbSchemaProvider> _logger;
        private readonly string _diskBackupPath;

        // ── Global Default Schema Cache (warmed by DbSchemaCacheWarmerService) ──
        private const string CACHE_KEY = "GlobalDbSchemaMap";
        private static readonly TimeSpan CACHE_DURATION = TimeSpan.FromHours(24);
        private static readonly SemaphoreSlim _diskSemaphore = new SemaphoreSlim(1, 1);

        // ── Per-Request / Multi-Tenant Schema Cache ──────────────────────────────
        // Key   = MD5(connectionString + "|" + sorted view names)
        // Value = { "offermasterview" → { "comid", "employeename", ... }, ... }
        //
        // ConcurrentDictionary: آمن لمئات الطلبات في نفس الوقت بدون lock يدوي.
        private static readonly ConcurrentDictionary<string, Dictionary<string, HashSet<string>>>
            _connectionScopedCache = new(StringComparer.OrdinalIgnoreCase);

        // Semaphore لمنع طلبين يجلبان نفس الـ Schema في نفس اللحظة (Double-Fetch Prevention)
        private static readonly SemaphoreSlim _fetchSemaphore = new SemaphoreSlim(1, 1);

        private const int DB_COMMAND_TIMEOUT_SECONDS = 15;

        public DbSchemaProvider(
            IConfiguration configuration,
            IMemoryCache memoryCache,
            ILogger<DbSchemaProvider> logger)
        {
            _memoryCache = memoryCache;
            _logger = logger;

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var appDataDir = Path.Combine(baseDir, "App_Data");
            Directory.CreateDirectory(appDataDir);
            _diskBackupPath = Path.Combine(appDataDir, "schema_backup.json");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  PATH 1: Default Global Schema (warmed at startup)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// يجلب خريطة الـ Schema من Memory Cache أو Disk Fallback.
        /// لا يتصل بقاعدة البيانات أبداً — فقط يقرأ من الذاكرة أو الملف.
        /// </summary>
        public async Task<Dictionary<string, HashSet<string>>> GetSchemaMapAsync(CancellationToken ct = default, bool forceRefresh = false)
        {
            if (!forceRefresh && _memoryCache.TryGetValue(CACHE_KEY, out Dictionary<string, HashSet<string>>? cachedSchema) && cachedSchema != null)
                return cachedSchema;

            _logger.LogWarning("[DbSchemaProvider] Memory cache MISS. Attempting Disk Fallback...");

            var diskSchema = await LoadFromDiskFallbackAsync(ct);
            if (diskSchema != null && diskSchema.Count > 0)
            {
                _logger.LogInformation("[DbSchemaProvider] ✓ Disk Fallback loaded successfully. Tables: {Count}", diskSchema.Count);
                _memoryCache.Set(CACHE_KEY, diskSchema, CACHE_DURATION);
                return diskSchema;
            }

            _logger.LogWarning("[DbSchemaProvider] ⚠️ No Disk Fallback available. Returning empty schema (Sanitizer will skip validation).");
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  PATH 2: Per-Request Targeted Schema (lazy, multi-tenant)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// يجلب Schema مستهدفة فقط للـ Views المطلوبة من Connection مخصص.
        /// ✅ Cache HIT  → يرجع فوراً من _connectionScopedCache (0ms).
        /// 🔴 Cache MISS → يتصل بالـ DB، يجلب فقط أعمدة هذه الـ Views، يخزن، ويرجع.
        /// </summary>
        public async Task<Dictionary<string, HashSet<string>>> GetSchemaForViewsAsync(
            string connectionString,
            IEnumerable<string> viewNames,
            CancellationToken ct = default)
        {
            var viewList = viewNames.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.ToLower()).Distinct().OrderBy(v => v).ToList();

            if (!viewList.Any())
            {
                _logger.LogWarning("[DbSchemaProvider] GetSchemaForViewsAsync called with no view names. Returning empty.");
                return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            }

            // ── الخطوة 1: احسب الـ Cache Key ──────────────────────────────────────
            // MD5(connectionString + "|" + "view1,view2,view3") → مفتاح قصير وفريد
            var cacheKey = ComputeCacheKey(connectionString, viewList);

            // ── الخطوة 2: Cache HIT Check ──────────────────────────────────────────
            if (_connectionScopedCache.TryGetValue(cacheKey, out var cached))
            {
                _logger.LogInformation("[DbSchemaProvider] ⚡ Targeted Schema Cache HIT for views: [{Views}]", string.Join(", ", viewList));
                return cached;
            }

            // ── الخطوة 3: Cache MISS → Fetch from DB ──────────────────────────────
            // نستخدم SemaphoreSlim لمنع طلبين يجلبان نفس الـ Schema في نفس اللحظة
            await _fetchSemaphore.WaitAsync(ct);
            try
            {
                // Double-check بعد الانتظار (ربما جلبه طلب آخر بينما كنا ننتظر)
                if (_connectionScopedCache.TryGetValue(cacheKey, out cached))
                {
                    _logger.LogInformation("[DbSchemaProvider] ⚡ Targeted Schema Cache HIT (after semaphore wait) for views: [{Views}]", string.Join(", ", viewList));
                    return cached;
                }

                _logger.LogInformation("[DbSchemaProvider] 🔴 Cache MISS. Fetching targeted schema for views: [{Views}]", string.Join(", ", viewList));

                var schema = await FetchTargetedSchemaAsync(connectionString, viewList, ct);
                _connectionScopedCache[cacheKey] = schema;

                _logger.LogInformation("[DbSchemaProvider] ✅ Targeted schema fetched and cached. Views: {ViewCount}, Total columns: {ColCount}",
                    schema.Count, schema.Values.Sum(s => s.Count));

                return schema;
            }
            finally
            {
                _fetchSemaphore.Release();
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  CacheWarmer Methods (Global Default Path only)
        // ═══════════════════════════════════════════════════════════════════

        public void UpdateSchemaCache(Dictionary<string, HashSet<string>> schemaMap)
        {
            if (schemaMap == null || schemaMap.Count == 0)
            {
                _logger.LogWarning("[DbSchemaProvider] Attempted to update cache with null/empty schema. Skipping.");
                return;
            }
            _memoryCache.Set(CACHE_KEY, schemaMap, CACHE_DURATION);
            _logger.LogInformation("[DbSchemaProvider] ✓ Memory Cache updated. Tables: {Count}", schemaMap.Count);
        }

        public async Task SaveSchemaToDiskBackupAsync(Dictionary<string, HashSet<string>> schemaMap)
        {
            if (schemaMap == null || schemaMap.Count == 0) return;

            await _diskSemaphore.WaitAsync();
            try
            {
                var serializableMap = new Dictionary<string, List<string>>();
                foreach (var kvp in schemaMap)
                    serializableMap[kvp.Key] = new List<string>(kvp.Value);

                var json = JsonSerializer.Serialize(serializableMap, new JsonSerializerOptions { WriteIndented = false });
                await File.WriteAllTextAsync(_diskBackupPath, json);
                _logger.LogInformation("[DbSchemaProvider] 💾 Disk Backup saved. Size: {Size:N0} bytes", json.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DbSchemaProvider] Failed to save Disk Backup.");
            }
            finally
            {
                _diskSemaphore.Release();
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Private Helpers
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// يجلب أعمدة Views محددة فقط من DB مستهدف.
        /// SQL مُحسَّن: يجلب فقط الـ Views المطلوبة، لا كل الجداول.
        /// </summary>
        private async Task<Dictionary<string, HashSet<string>>> FetchTargetedSchemaAsync(
            string connectionString,
            IList<string> viewNames,
            CancellationToken ct)
        {
            // استعلام مستهدف: يجلب فقط الـ Views المطلوبة
            var sql = @"
                SELECT LOWER(o.name) AS TableName, LOWER(c.name) AS ColumnName
                FROM sys.objects o
                INNER JOIN sys.columns c ON o.object_id = c.object_id
                WHERE o.type IN ('U', 'V')
                AND LOWER(o.name) IN @ViewNames
                ORDER BY o.name, c.column_id";

            using var connection = new SqlConnection(connectionString);

            var flatData = await connection.QueryAsync<(string TableName, string ColumnName)>(
                new CommandDefinition(sql,
                    parameters: new { ViewNames = viewNames },
                    commandTimeout: DB_COMMAND_TIMEOUT_SECONDS,
                    cancellationToken: ct));

            var schemaMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in flatData)
            {
                if (!schemaMap.ContainsKey(row.TableName))
                    schemaMap[row.TableName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                schemaMap[row.TableName].Add(row.ColumnName);
            }

            return schemaMap;
        }

        /// <summary>
        /// يحسب Cache Key فريد بناءً على Connection String + أسماء الـ Views.
        /// يستخدم MD5 لتقليل حجم الـ Key.
        /// </summary>
        private static string ComputeCacheKey(string connectionString, IEnumerable<string> sortedViewNames)
        {
            var raw = $"{connectionString}|{string.Join(",", sortedViewNames)}";
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes);
        }

        /// <summary>
        /// يقرأ النسخة الاحتياطية من الـ Disk إذا كانت موجودة.
        /// </summary>
        private async Task<Dictionary<string, HashSet<string>>?> LoadFromDiskFallbackAsync(CancellationToken ct)
        {
            try
            {
                if (!File.Exists(_diskBackupPath))
                {
                    _logger.LogWarning("[DbSchemaProvider] No Disk Backup file found at: {Path}", _diskBackupPath);
                    return null;
                }

                var json = await File.ReadAllTextAsync(_diskBackupPath, ct);
                var deserialized = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
                if (deserialized == null) return null;

                var schemaMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in deserialized)
                    schemaMap[kvp.Key] = new HashSet<string>(kvp.Value, StringComparer.OrdinalIgnoreCase);

                return schemaMap;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DbSchemaProvider] Failed to load Disk Backup.");
                return null;
            }
        }
    }
}
