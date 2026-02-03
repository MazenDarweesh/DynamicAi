using Dapper;
using DynamicAi.Application.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// التنفيذ الفعلي لـ ISandboxedSqlExecutor.
    /// يستخدم Dapper لتنفيذ الاستعلامات بصلاحيات قراءة فقط.
    /// </summary>
    public class SandboxedSqlExecutor : ISandboxedSqlExecutor
    {
        private readonly string _readOnlyConnectionString;
        private readonly ILogger<SandboxedSqlExecutor> _logger;
        private const int COMMAND_TIMEOUT_SECONDS = 30;

        public SandboxedSqlExecutor(
            IConfiguration configuration,
            ILogger<SandboxedSqlExecutor> logger)
        {
            _readOnlyConnectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("DefaultConnection string is not configured.");
            _logger = logger;
        }

        /// <summary>
        /// (مُعدل) أصبح الآن يقبل قاموس الباراميترات للتنفيذ الآمن.
        /// </summary>
        public async Task<IEnumerable<dynamic>> ExecuteSqlAsync(string rewrittenSql, Dictionary<string, object> parameters)
        {
            // ============================================================================
            // [DIAGNOSTIC] طباعة الـ SQL الفعلي المُنفذ
            // ============================================================================
            _logger.LogWarning("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogWarning("║         📋 [SQL EXECUTOR] - FINAL QUERY BEFORE EXECUTION              ║");
            _logger.LogWarning("╚════════════════════════════════════════════════════════════════════════╝");
            _logger.LogWarning("\n[SQL Query] Characters: {Length}", rewrittenSql.Length);
            _logger.LogWarning("\n[SQL Parameters] Count: {Count}", parameters.Count);

            if (parameters.Any())
            {
                _logger.LogWarning("\n[Parameters]:");
                foreach (var param in parameters)
                {
                    _logger.LogWarning("  - @{Key} = {Value}", param.Key, param.Value);
                }
            }

            _logger.LogWarning("\n[Full SQL Query]:\n{Sql}\n", rewrittenSql);
            _logger.LogWarning("╚════════════════════════════════════════════════════════════════════════╝");

            await using var connection = new SqlConnection(_readOnlyConnectionString);

            // Dapper سيقوم تلقائياً بربط الباراميترات بشكل آمن
            return await connection.QueryAsync(rewrittenSql, parameters);
        }
    }
}