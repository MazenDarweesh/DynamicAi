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
        public Task ValidateSqlAsync(string rawSql, string tenantId)
        {
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║              [Layer 3: SQL VALIDATOR] - STARTING VALIDATION            ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            _logger.LogInformation("[SqlValidator] TenantId: {TenantId}", tenantId);
            _logger.LogInformation("[SqlValidator] SQL Length: {SqlLength} characters", rawSql.Length);
            _logger.LogDebug("[SqlValidator] SQL Preview: {SqlPreview}...", rawSql.Substring(0, Math.Min(200, rawSql.Length)));

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
}