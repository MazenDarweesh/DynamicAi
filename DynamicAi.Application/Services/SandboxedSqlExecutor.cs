using Dapper;
using DynamicAi.Application.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// (تنفيذ Layer 5)
    /// يستخدم Dapper ومستخدم "القراءة فقط" (SchemaPipelineUser)
    /// لتنفيذ الـ Query بأمان.
    /// </summary>
    public class SandboxedSqlExecutor : ISandboxedSqlExecutor
    {
        private readonly string _readOnlyConnectionString;
        private const int COMMAND_TIMEOUT_SECONDS = 30; // (مهلة 30 ثانية)

        public SandboxedSqlExecutor(IConfiguration configuration)
        {
            // (هام) يستخدم Connection String المستخدم "للقراءة فقط"
            // الذي أنشأناه لمشروع SchemaBootstrapper
            _readOnlyConnectionString = configuration.GetConnectionString("DefaultConnection");
        }

        /// <summary>
        /// ينفذ الـ SQL الآمن (الذي مر من Layer 3).
        /// </summary>
        public async Task<IEnumerable<dynamic>> ExecuteSqlAsync(string safeSql, string tenantId)
        {
            await using var connection = new SqlConnection(_readOnlyConnectionString);

            // (هام) نقوم بتمرير الـ tenantId كباراميتر.
            // هذا يفترض أن الـ AI (في Layer 2) كان ذكياً كفاية
            // وكتب الـ SQL هكذا: "... WHERE TenantId = @TenantId"
            var parameters = new { TenantId = tenantId };

            // (Dapper يقوم بربط الباراميترات تلقائياً، مما يوفر حماية من SQL Injection)
            return await connection.QueryAsync(
                safeSql,
                parameters,
                commandTimeout: COMMAND_TIMEOUT_SECONDS
            );
        }
    }
}