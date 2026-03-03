using DynamicAi.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicAi.Application.Services.SqlEngine
{
    /// <summary>
    /// (تنفيذ Layer 2.7 Helper)
    /// يوفر هيكل قاعدة البيانات (الجداول والأعمدة) من Memory Cache أو Disk Fallback.
    /// لا يتصل بقاعدة البيانات أبداً أثناء طلبات المستخدمين.
    /// خدمة DbSchemaCacheWarmerService في الخلفية هي المسؤولة الوحيدة عن جلب البيانات من DB.
    /// </summary>
    public class DbSchemaProvider : IDbSchemaProvider
    {
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<DbSchemaProvider> _logger;
        private readonly string _diskBackupPath;
        private const string CACHE_KEY = "GlobalDbSchemaMap";
        private static readonly TimeSpan CACHE_DURATION = TimeSpan.FromHours(24);

        private static readonly SemaphoreSlim _diskSemaphore = new SemaphoreSlim(1, 1);

        public DbSchemaProvider(
            IConfiguration configuration,
            IMemoryCache memoryCache,
            ILogger<DbSchemaProvider> logger)
        {
            _memoryCache = memoryCache;
            _logger = logger;

            // تحديد مسار ملف النسخة الاحتياطية
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var appDataDir = Path.Combine(baseDir, "App_Data");
            Directory.CreateDirectory(appDataDir); // إنشاء المجلد إذا لم يكن موجوداً
            _diskBackupPath = Path.Combine(appDataDir, "schema_backup.json");
        }

        /// <summary>
        /// يجلب خريطة الـ Schema من Memory Cache أو Disk Fallback.
        /// لا يتصل بقاعدة البيانات أبداً — فقط يقرأ من الذاكرة أو الملف.
        /// </summary>
        public async Task<Dictionary<string, HashSet<string>>> GetSchemaMapAsync(CancellationToken ct = default, bool forceRefresh = false)
        {
            // 1. محاولة الجلب من Memory Cache (0ms)
            if (!forceRefresh && _memoryCache.TryGetValue(CACHE_KEY, out Dictionary<string, HashSet<string>>? cachedSchema) && cachedSchema != null)
            {
                return cachedSchema;
            }

            // 2. Memory Cache فارغ → محاولة الجلب من Disk Fallback (< 1ms)
            _logger.LogWarning("[DbSchemaProvider] Memory cache MISS. Attempting Disk Fallback...");

            var diskSchema = await LoadFromDiskFallbackAsync(ct);
            if (diskSchema != null && diskSchema.Count > 0)
            {
                _logger.LogInformation("[DbSchemaProvider] ✓ Disk Fallback loaded successfully. Tables: {Count}", diskSchema.Count);
                // حفظ في Memory Cache للطلبات القادمة
                _memoryCache.Set(CACHE_KEY, diskSchema, CACHE_DURATION);
                return diskSchema;
            }

            // 3. لا يوجد حتى ملف Disk → نرجع Dictionary فارغ
            // الـ Sanitizer سيتخطى التحقق ويمرر الـ SQL كما هو (Fail-Open)
            _logger.LogWarning("[DbSchemaProvider] ⚠️ No Disk Fallback available. Returning empty schema (Sanitizer will skip validation).");
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// يحدث الـ Memory Cache مباشرة. يستخدم من قبل CacheWarmer فقط.
        /// </summary>
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

        /// <summary>
        /// يحفظ نسخة احتياطية من الـ Schema على الـ Disk. يستخدم من قبل CacheWarmer فقط.
        /// </summary>
        public async Task SaveSchemaToDiskBackupAsync(Dictionary<string, HashSet<string>> schemaMap)
        {
            if (schemaMap == null || schemaMap.Count == 0) return;

            await _diskSemaphore.WaitAsync();
            try
            {
                // تحويل HashSet إلى List للـ Serialization
                var serializableMap = new Dictionary<string, List<string>>();
                foreach (var kvp in schemaMap)
                {
                    serializableMap[kvp.Key] = new List<string>(kvp.Value);
                }

                var json = JsonSerializer.Serialize(serializableMap, new JsonSerializerOptions
                {
                    WriteIndented = false // توفير مساحة
                });

                await File.WriteAllTextAsync(_diskBackupPath, json);
                _logger.LogInformation("[DbSchemaProvider] 💾 Disk Backup saved. Size: {Size:N0} bytes, Path: {Path}",
                    json.Length, _diskBackupPath);
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

        #region Private Helpers

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

                // تحويل List إلى HashSet
                var schemaMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in deserialized)
                {
                    schemaMap[kvp.Key] = new HashSet<string>(kvp.Value, StringComparer.OrdinalIgnoreCase);
                }

                return schemaMap;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DbSchemaProvider] Failed to load Disk Backup.");
                return null;
            }
        }

        #endregion
    }
}
