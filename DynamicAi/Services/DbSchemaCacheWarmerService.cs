using Dapper;
using DynamicAi.Application.Interfaces;
using Microsoft.Data.SqlClient;
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
    /// خدمة تعمل في الخلفية — المسؤول الوحيد عن الاتصال بقاعدة البيانات لجلب الـ Schema.
    /// يحفظ النتيجة في Memory Cache (عبر DbSchemaProvider) وعلى الـ Disk (نسخة احتياطية).
    /// لضمان أداء فوري للمستخدمين (0ms cache latency).
    /// </summary>
    public class DbSchemaCacheWarmerService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<DbSchemaCacheWarmerService> _logger;
        private readonly string _connectionString;
        private readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(60);     // فترة التحديث الطبيعي
        private readonly TimeSpan _failureRetryInterval = TimeSpan.FromSeconds(20); // فترة إعادة المحاولة عند الفشل
        private const int DB_COMMAND_TIMEOUT_SECONDS = 15; // Timeout قصير لمنع التعليق

        public DbSchemaCacheWarmerService(
            IServiceProvider serviceProvider,
            IConfiguration configuration,
            ILogger<DbSchemaCacheWarmerService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("DefaultConnection string is not configured.");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            if (!config.GetValue<bool>("Features:DbSchemaWarmerEnabled", true))
            {
                _logger.LogInformation("[DbSchemaCacheWarmer] 🛑 Service is disabled in config. Service bypassed.");
                return;
            }

            _logger.LogInformation("[DbSchemaCacheWarmer] Service is starting...");

            var fetchSuccess = false;

            while (!stoppingToken.IsCancellationRequested)
            {
                fetchSuccess = false;

                try
                {
                    _logger.LogInformation("[DbSchemaCacheWarmer] Warming up DB Schema Cache...");

                    // 1. جلب الـ Schema من الداتابيز مباشرة (مع Timeout قصير)
                    var schemaMap = await FetchSchemaFromDatabaseAsync(stoppingToken);

                    if (schemaMap != null && schemaMap.Count > 0)
                    {
                        // 2. تحديث Memory Cache عبر الـ Provider
                        var schemaProvider = _serviceProvider.GetRequiredService<IDbSchemaProvider>();
                        schemaProvider.UpdateSchemaCache(schemaMap);

                        // 3. حفظ نسخة احتياطية على الـ Disk
                        await schemaProvider.SaveSchemaToDiskBackupAsync(schemaMap);

                        _logger.LogInformation(
                            "[DbSchemaCacheWarmer] ✅ Cache warmed successfully. Tables: {Count}. Next update in {Minutes} minutes.",
                            schemaMap.Count, _refreshInterval.TotalMinutes);
                        fetchSuccess = true;
                    }
                    else
                    {
                        _logger.LogWarning("[DbSchemaCacheWarmer] ⚠️ Schema fetch returned empty result. Keeping existing cache. Retrying in {Minutes} minutes.", _failureRetryInterval.TotalMinutes);
                    }
                }
                catch (SqlException ex)
                {
                    _logger.LogError(ex, "[DbSchemaCacheWarmer] 🔴 SQL Error while warming cache. DB might be unreachable. Will retry in {Minutes} minutes.", _failureRetryInterval.TotalMinutes);
                }
                catch (TaskCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Application is shutting down — exit gracefully
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[DbSchemaCacheWarmer] Error occurred while warming DB Schema Cache. Will retry in {Minutes} minutes.", _failureRetryInterval.TotalMinutes);
                }

                // نجاح → انتظر 60 دقيقة للتحديث التالي، فشل → أعد المحاولة كل دقيقتين
                var waitTime = fetchSuccess ? _refreshInterval : _failureRetryInterval;
                await Task.Delay(waitTime, stoppingToken);
            }

            _logger.LogInformation("[DbSchemaCacheWarmer] Service is stopping.");
        }

        /// <summary>
        /// يتصل بقاعدة البيانات مباشرة ويجلب خريطة الجداول والأعمدة.
        /// يستخدم Timeout قصير (15 ثانية) لمنع التعليق الطويل.
        /// </summary>
        private async Task<Dictionary<string, HashSet<string>>?> FetchSchemaFromDatabaseAsync(CancellationToken ct)
        {
            var sql = @"
                SELECT 
                    LOWER(o.name) as TableName, 
                    LOWER(c.name) as ColumnName
                FROM sys.objects o
                INNER JOIN sys.columns c ON o.object_id = c.object_id
                WHERE o.type IN ('U', 'V') -- U = USER_TABLE, V = VIEW
                AND o.name <> 'sysdiagrams'
                ORDER BY o.name";

            using var connection = new SqlConnection(_connectionString);
            var flatData = await connection.QueryAsync<(string TableName, string ColumnName)>(
                new CommandDefinition(sql, commandTimeout: DB_COMMAND_TIMEOUT_SECONDS, cancellationToken: ct));

            var schemaMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in flatData)
            {
                if (!schemaMap.ContainsKey(row.TableName))
                {
                    schemaMap[row.TableName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                schemaMap[row.TableName].Add(row.ColumnName);
            }

            _logger.LogInformation("[DbSchemaCacheWarmer] DB Schema fetched. Tables found: {Count}", schemaMap.Count);
            return schemaMap;
        }
    }
}
