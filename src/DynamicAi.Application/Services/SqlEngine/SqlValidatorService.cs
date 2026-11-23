using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Security;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// (تنفيذ Layer 3)
    /// يستخدم TSqlFragmentVisitor (ScriptDom) لتحليل الـ SQL "الخام"
    /// وتطبيق "الزائر الأمني" (SecurityVisitor) عليه.
    /// 
    /// النهج: Blacklist-Only (لا Whitelist)
    /// - الـ AI مقيد بالـ Prompt لاستخدام Tables من الـ Context فقط
    /// - نحظر DDL/DML الخطرة عبر SecurityVisitor
    /// </summary>
    public class SqlValidatorService : ISqlValidatorService
    {
        private readonly ILogger<SqlValidatorService> _logger;

        public SqlValidatorService(ILogger<SqlValidatorService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// يقوم بتحليل الـ SQL والتحقق من صحة بناء الجملة (Syntax) والأمان (Security).
        /// </summary>
        public Task ValidateSqlAsync(string rawSql, string tenantId, CancellationToken ct = default)
        {
            _logger.LogInformation("[SqlValidator] Starting validation for TenantId={TenantId}, SqlChars={SqlLength}", tenantId, rawSql.Length);
            _logger.LogDebug("[SqlValidator] Raw SQL: {RawSql}", rawSql);

            // STEP 1: Parse SQL (Syntax Validation)
            TSqlParser parser = new TSql160Parser(false);
            IList<ParseError> errors;
            TSqlFragment fragment;

            using (var reader = new StringReader(rawSql))
            {
                fragment = parser.Parse(reader, out errors);
            }

            if (errors.Any())
            {
                foreach (var error in errors)
                {
                    _logger.LogError("[SqlValidator] Syntax error at Line {Line}, Column {Column}: {Message}", error.Line, error.Column, error.Message);
                }

                _logger.LogError("[SqlValidator] Validation failed with {ErrorCount} syntax error(s)", errors.Count);
                throw new InvalidDataException($"SQL Syntax Error: {errors.First().Message}");
            }

            // STEP 2.5: TOP + OFFSET Check (Tree-Wide, SQL Server Error 10741)
            var topOffsetChecker = new TopOffsetChecker();
            fragment.Accept(topOffsetChecker);

            if (topOffsetChecker.HasTopAndOffset)
            {
                _logger.LogError("[SqlValidator] Validation failed: illegal TOP + OFFSET combination detected");
                throw new InvalidDataException(
                    "SQL Validation Failed: The query contains both TOP and OFFSET clauses. " +
                    "T-SQL does not allow combining TOP with OFFSET (even in nested queries). " +
                    "Please use OFFSET/FETCH ONLY for paging.");
            }

            // STEP 3: Security Validation (Blacklist-Only AST Traversal)
            try
            {
                SecurityVisitor.ValidateReadOnlyScript(fragment);
            }
            catch (Exception secEx)
            {
                _logger.LogError("[SqlValidator] Security validation failed: {Message}", secEx.Message);
                throw;
            }

            _logger.LogInformation("[SqlValidator] Validation passed for TenantId={TenantId}", tenantId);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Visitor that scans the ENTIRE SQL tree for TOP and OFFSET usage.
    /// Used to detect illegal combinations across nested queries.
    /// </summary>
    public class TopOffsetChecker : TSqlFragmentVisitor
    {
        public bool HasTop { get; private set; }
        public bool HasOffset { get; private set; }
        public bool HasTopAndOffset => HasTop && HasOffset;

        public override void Visit(QuerySpecification node)
        {
            if (node.TopRowFilter != null)
            {
                HasTop = true;
            }

            if (node.OffsetClause != null)
            {
                HasOffset = true;
            }

            base.Visit(node);
        }
    }
}