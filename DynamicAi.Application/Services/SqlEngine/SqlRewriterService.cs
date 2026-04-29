using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// (تنفيذ Layer 4.8)
    /// يستخدم ScriptDom Visitor لتنفيذ التحصينات الثلاثة.
    /// </summary>
    public class SqlRewriterService : ISqlRewriterService
    {
        private const int MAX_ROWS_LIMIT = 1000; // (الحد الأقصى الافتراضي للصفوف)
        private readonly ILogger<SqlRewriterService> _logger;

        public SqlRewriterService(ILogger<SqlRewriterService> logger)
        {
            _logger = logger;
        }

        public Task<RewrittenSqlResult> RewriteAsync(string safeSql, string tenantId, int? comID = 1, string? tailCondition = null, CancellationToken ct = default)
        {
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║             [Layer 4.8: SQL REWRITER] - HARDENING                      ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");
            // 1. إعداد المحلل (Parser)
            TSqlParser parser = new TSql160Parser(false);
            IList<ParseError> errors;
            TSqlFragment fragment;
            using (var reader = new StringReader(safeSql))
            {
                fragment = parser.Parse(reader, out errors);
            }

            // 2. إعداد الـ Visitor والباراميترات
            var parameters = new Dictionary<string, object>();

            // إضافة ComID إلى الباراميترات إذا كان موجوداً في SQL
            if (comID.HasValue && safeSql.Contains("@CurrentCompanyId", StringComparison.OrdinalIgnoreCase))
            {
                parameters["@CurrentCompanyId"] = comID.Value;
                _logger.LogInformation("[SqlRewriter] Added @CurrentCompanyId parameter with value: {CompanyId}", comID.Value);
            }

            var visitor = new SqlHardeningVisitor(parameters, tenantId, MAX_ROWS_LIMIT, tailCondition);

            // 3. تطبيق التحصينات (الزيارة)
            fragment.Accept(visitor);

            // 4. استخراج الـ SQL "المُحصّن"
            // FIX: Use Sql160ScriptGenerator (without the 'T' prefix) for version 170
            var sqlGenerator = new Sql160ScriptGenerator();
            string rewrittenSql;
            sqlGenerator.GenerateScript(fragment, out rewrittenSql);

            // 5. إرجاع النتيجة
            var result = new RewrittenSqlResult(rewrittenSql, visitor.Parameters);
            return Task.FromResult(result);
        }
    }
}