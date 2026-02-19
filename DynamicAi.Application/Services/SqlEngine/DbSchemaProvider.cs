using Dapper;
using DynamicAi.Application.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DynamicAi.Application.Services.SqlEngine
{
    /// <summary>
    /// (تنفيذ Layer 2.7 Helper)
    /// يجلب هيكل قاعدة البيانات (الجداول والأعمدة) باستخدام INFORMATION_SCHEMA.
    /// </summary>
    public class DbSchemaProvider : IDbSchemaProvider
    {
        private readonly string _connectionString;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<DbSchemaProvider> _logger;
        private const string CACHE_KEY = "GlobalDbSchemaMap";
        private static readonly TimeSpan CACHE_DURATION = TimeSpan.FromMinutes(10); // تخزين الكاش لمدة 10 دقائق

        public DbSchemaProvider(
            IConfiguration configuration,
            IMemoryCache memoryCache,
            ILogger<DbSchemaProvider> logger)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("DefaultConnection string is not configured.");
            _memoryCache = memoryCache;
            _logger = logger;
        }

        public async Task<Dictionary<string, HashSet<string>>> GetSchemaMapAsync()
        {
            // محاولة الجلب من الكاش أولاً
            if (_memoryCache.TryGetValue(CACHE_KEY, out Dictionary<string, HashSet<string>> cachedSchema))
            {
                // _logger.LogDebug("[DbSchemaProvider] Returns cached schema map.");
                return cachedSchema;
            }

            _logger.LogInformation("[DbSchemaProvider] Cache MISS. Fetching schema from DB...");

            using var connection = new SqlConnection(_connectionString);
            
            // استعلام سريع لجلب كل الجداول والأعمدة
            // نستبعد جداول النظام sysdiagrams ونركز على BASE TABLE و VIEW
            var sql = @"
                SELECT 
                    LOWER(t.TABLE_NAME) as TableName, 
                    LOWER(c.COLUMN_NAME) as ColumnName
                FROM INFORMATION_SCHEMA.TABLES t
                INNER JOIN INFORMATION_SCHEMA.COLUMNS c ON t.TABLE_NAME = c.TABLE_NAME AND t.TABLE_SCHEMA = c.TABLE_SCHEMA
                WHERE t.TABLE_TYPE IN ('BASE TABLE', 'VIEW')
                AND t.TABLE_NAME <> 'sysdiagrams'
                ORDER BY t.TABLE_NAME";

            var flatData = await connection.QueryAsync<(string TableName, string ColumnName)>(sql);

            // تحويل البيانات إلى Dictionary<TableName, HashSet<ColumnName>>
            var schemaMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in flatData)
            {
                if (!schemaMap.ContainsKey(row.TableName))
                {
                    schemaMap[row.TableName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                schemaMap[row.TableName].Add(row.ColumnName);
            }

            _logger.LogInformation("[DbSchemaProvider] Schema fetched successfully. Tables found: {Count}", schemaMap.Count);

            // حفظ في الكاش
            _memoryCache.Set(CACHE_KEY, schemaMap, CACHE_DURATION);

            return schemaMap;
        }
    }
}
