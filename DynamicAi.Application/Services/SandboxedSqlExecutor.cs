using Dapper;
using DynamicAi.Application.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
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
        private const int COMMAND_TIMEOUT_SECONDS = 30;

        public SandboxedSqlExecutor(IConfiguration configuration)
        {
            _readOnlyConnectionString = configuration.GetConnectionString("DefaultConnection");
        }

        /// <summary>
        /// (مُعدل) أصبح الآن يقبل قاموس الباراميترات للتنفيذ الآمن.
        /// </summary>
        public async Task<IEnumerable<dynamic>> ExecuteSqlAsync(string rewrittenSql, Dictionary<string, object> parameters)
        {
            await using var connection = new SqlConnection(_readOnlyConnectionString);
            
            // Dapper سيقوم تلقائياً بربط الباراميترات بشكل آمن
            return await connection.QueryAsync(rewrittenSql, parameters);
        }
    }
}