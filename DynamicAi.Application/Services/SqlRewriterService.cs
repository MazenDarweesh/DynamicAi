using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// (تنفيذ Layer 4)
    /// يستخدم ScriptDom Visitor لتنفيذ التحصينات الثلاثة.
    /// </summary>
    public class SqlRewriterService : ISqlRewriterService
    {
        private const int MAX_ROWS_LIMIT = 1000; // (الحد الأقصى الافتراضي للصفوف)

        public Task<RewrittenSqlResult> RewriteAsync(string safeSql, string tenantId)
        {
            // 1. إعداد المحلل (Parser)
            TSqlParser parser = new TSql160Parser(false);
            IList<ParseError> errors;
            TSqlFragment fragment;
            using (var reader = new StringReader(safeSql))
            {
                fragment = parser.Parse(reader, out errors);
            }

            // (نفترض أن الـ Syntax سليم لأنه مر من Layer 3)

            // 2. إعداد الـ Visitor والباراميترات
            var parameters = new Dictionary<string, object>();
            var visitor = new SqlHardeningVisitor(parameters, tenantId, MAX_ROWS_LIMIT);

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