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
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║              [Layer 3: SQL VALIDATOR] - STARTING VALIDATION            ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            _logger.LogInformation("[SqlValidator] TenantId: {TenantId}", tenantId);
            _logger.LogInformation("[SqlValidator] SQL Length: {SqlLength} characters", rawSql.Length);

            // DEBUG: Print full SQL for debugging TOP+OFFSET issue
            _logger.LogDebug("[SqlValidator] ═══════════════════════ FULL SQL START ═══════════════════════");
            _logger.LogDebug("[SqlValidator] {RawSql}", rawSql);
            _logger.LogDebug("[SqlValidator] ═══════════════════════ FULL SQL END ═══════════════════════");

            // ============================================================================
            // STEP 1: تحديد المحلل (Parser)
            // ============================================================================
            _logger.LogInformation("[SqlValidator] STEP 1: Initializing TSql160Parser (SQL Server 2022)...");

            TSqlParser parser = new TSql160Parser(false);

            _logger.LogInformation("[SqlValidator] ✓ Parser initialized successfully.");

            // ============================================================================
            // STEP 2: التحقق من بناء الجملة (Syntax Validation)
            // ============================================================================
            _logger.LogInformation("[SqlValidator] STEP 2: Parsing SQL for syntax validation...");

            IList<ParseError> errors;
            TSqlFragment fragment;

            using (var reader = new StringReader(rawSql))
            {
                fragment = parser.Parse(reader, out errors);
            }

            // عرض نتيجة التحليل
            if (errors.Any())
            {
                _logger.LogError("[SqlValidator] ✗ SYNTAX VALIDATION FAILED!");
                _logger.LogError("[SqlValidator] Found {ErrorCount} syntax error(s):", errors.Count);

                foreach (var error in errors)
                {
                    _logger.LogError("  - Error at Line {Line}, Column {Column}: {Message}", error.Line, error.Column, error.Message);
                }

                _logger.LogError("╔════════════════════════════════════════════════════════════════════════╗");
                _logger.LogError("║              [Layer 3: SQL VALIDATOR] - VALIDATION FAILED             ║");
                _logger.LogError("╚════════════════════════════════════════════════════════════════════════╝");

                throw new InvalidDataException($"SQL Syntax Error: {errors.First().Message}");
            }

            _logger.LogInformation("[SqlValidator] ✓ Syntax validation passed. SQL is well-formed.");

            // ============================================================================
            // STEP 2.5: التحقق من TOP + OFFSET (Tree-Wide)
            // SQL Server Error 10741: Cannot combine TOP with OFFSET in same query/subquery
            // ============================================================================
            _logger.LogInformation("[SqlValidator] STEP 2.5: Checking for illegal TOP + OFFSET combination...");

            var topOffsetChecker = new TopOffsetChecker();
            fragment.Accept(topOffsetChecker);

            if (topOffsetChecker.HasTopAndOffset)
            {
                _logger.LogError("[SqlValidator] ✗ TOP + OFFSET VALIDATION FAILED!");
                _logger.LogError("[SqlValidator] SQL contains both TOP and OFFSET clauses (possibly in nested queries).");
                _logger.LogError("[SqlValidator] This combination is illegal in T-SQL (Error 10741).");

                _logger.LogError("╔════════════════════════════════════════════════════════════════════════╗");
                _logger.LogError("║           [Layer 3: SQL VALIDATOR] - TOP+OFFSET VIOLATION             ║");
                _logger.LogError("╚════════════════════════════════════════════════════════════════════════╝");

                throw new InvalidDataException(
                    "SQL Validation Failed: The query contains both TOP and OFFSET clauses. " +
                    "T-SQL does not allow combining TOP with OFFSET (even in nested queries). " +
                    "Please use OFFSET/FETCH ONLY for paging.");
            }

            _logger.LogInformation("[SqlValidator] ✓ TOP + OFFSET check passed.");

            // ============================================================================
            // STEP 3: التحقق الأمني (Security Validation) - ENABLED!
            // ============================================================================
            _logger.LogInformation("[SqlValidator] STEP 3: Performing security validation (Blacklist-Only)...");

            // إنشاء الزائر الأمني (بدون Whitelist)
            var securityVisitor = new SecurityVisitor();

            _logger.LogInformation("[SqlValidator] Initiating SecurityVisitor to traverse SQL AST...");
            _logger.LogInformation("[SqlValidator] Checking for: DELETE, UPDATE, INSERT, DROP, ALTER, CREATE, EXEC, etc.");

            try
            {
                // التجول في شجرة بناء الجملة (AST)
                fragment.Accept(securityVisitor);

                _logger.LogInformation("[SqlValidator] ✓ Security validation passed. No forbidden statements detected.");
            }
            catch (Exception secEx)
            {
                _logger.LogError("[SqlValidator] ✗ SECURITY VALIDATION FAILED!");
                _logger.LogError("[SqlValidator] Security violation detected: {Message}", secEx.Message);

                _logger.LogError("╔════════════════════════════════════════════════════════════════════════╗");
                _logger.LogError("║              [Layer 3: SQL VALIDATOR] - SECURITY VIOLATION            ║");
                _logger.LogError("╚════════════════════════════════════════════════════════════════════════╝");

                throw;
            }

            // ============================================================================
            // STEP 4: النجاح - الخروج
            // ============================================================================
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║           [Layer 3: SQL VALIDATOR] - VALIDATION SUCCESSFUL            ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

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