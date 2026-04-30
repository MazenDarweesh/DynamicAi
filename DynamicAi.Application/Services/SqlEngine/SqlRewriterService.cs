using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// (تنفيذ Layer 4.8)
    /// يستخدم ScriptDom Visitor لتنفيذ التحصينات.
    /// </summary>
    public class SqlRewriterService : ISqlRewriterService
    {
        private const int MAX_ROWS_LIMIT = 1000;
        private readonly ILogger<SqlRewriterService> _logger;

        public SqlRewriterService(ILogger<SqlRewriterService> logger)
        {
            _logger = logger;
        }

        public Task<RewrittenSqlResult> RewriteAsync(
            string safeSql,
            string tenantId,
            int? comID = 1,
            IEnumerable<TailConditionEntry>? tailConditions = null,
            CancellationToken ct = default)
        {
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║             [Layer 4.8: SQL REWRITER] - HARDENING                      ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            var entries = tailConditions?.ToList() ?? new List<TailConditionEntry>();

            if (entries.Any())
            {
                _logger.LogInformation("[SqlRewriter] 🛡️ TailCondition entries to verify ({Count}):", entries.Count);
                foreach (var e in entries)
                {
                    _logger.LogInformation("[SqlRewriter]   • View={ViewName} | Alias={Alias} | Condition={Condition}",
                        e.ViewName, e.SqlAlias, e.Condition);
                }
            }
            else
            {
                _logger.LogInformation("[SqlRewriter] ℹ️ No TailCondition entries provided.");
            }

            // 1. Parse SQL
            TSqlParser parser = new TSql160Parser(false);
            IList<ParseError> errors;
            TSqlFragment fragment;
            using (var reader = new StringReader(safeSql))
            {
                fragment = parser.Parse(reader, out errors);
            }

            // 2. Prepare parameters & visitor
            var parameters = new Dictionary<string, object>();

            if (comID.HasValue && safeSql.Contains("@CurrentCompanyId", StringComparison.OrdinalIgnoreCase))
            {
                parameters["@CurrentCompanyId"] = comID.Value;
                _logger.LogInformation("[SqlRewriter] Added @CurrentCompanyId parameter with value: {CompanyId}", comID.Value);
            }

            var visitor = new SqlHardeningVisitor(parameters, tenantId, MAX_ROWS_LIMIT, entries);

            // 3. Apply hardening (ExplicitVisit-driven traversal)
            fragment.AcceptChildren(visitor);

            // 4. Generate hardened SQL
            var sqlGenerator = new Sql160ScriptGenerator();
            sqlGenerator.GenerateScript(fragment, out string rewrittenSql);

            _logger.LogInformation("[SqlRewriter] ✅ Hardening complete. Output SQL length: {Len} chars", rewrittenSql.Length);

            return Task.FromResult(new RewrittenSqlResult(rewrittenSql, visitor.Parameters));
        }
    }
}