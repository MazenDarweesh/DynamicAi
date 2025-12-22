using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Security;
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
        public SqlValidatorService()
        {
            // Constructor فارغ - لا نحتاج Whitelist
        }

        /// <summary>
        /// يقوم بتحليل الـ SQL والتحقق من صحة بناء الجملة (Syntax) والأمان (Security).
        /// </summary>
        public Task ValidateSqlAsync(string rawSql, string tenantId)
        {
            // ============================================================================
            // [LOGGING] بداية عملية التحقق
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║              [Layer 3: SQL VALIDATOR] - STARTING VALIDATION            ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
            Console.ResetColor();

            Console.WriteLine($"[SqlValidator] TenantId: {tenantId}");
            Console.WriteLine($"[SqlValidator] SQL Length: {rawSql.Length} characters");
            Console.WriteLine($"[SqlValidator] SQL Preview (first 200 chars):");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  {rawSql.Substring(0, Math.Min(200, rawSql.Length))}...");
            Console.ResetColor();

            // ============================================================================
            // STEP 1: تحديد المحلل (Parser)
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[SqlValidator] STEP 1: Initializing TSql160Parser (SQL Server 2022)...");
            Console.ResetColor();

            TSqlParser parser = new TSql160Parser(false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[SqlValidator] ✓ Parser initialized successfully.");
            Console.ResetColor();

            // ============================================================================
            // STEP 2: التحقق من بناء الجملة (Syntax Validation)
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[SqlValidator] STEP 2: Parsing SQL for syntax validation...");
            Console.ResetColor();

            IList<ParseError> errors;
            TSqlFragment fragment;

            using (var reader = new StringReader(rawSql))
            {
                fragment = parser.Parse(reader, out errors);
            }

            // عرض نتيجة التحليل
            if (errors.Any())
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[SqlValidator] ✗ SYNTAX VALIDATION FAILED!");
                Console.WriteLine($"[SqlValidator] Found {errors.Count} syntax error(s):");
                Console.ResetColor();

                foreach (var error in errors)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  - Error at Line {error.Line}, Column {error.Column}:");
                    Console.WriteLine($"    Message: {error.Message}");
                    Console.ResetColor();
                }

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║              [Layer 3: SQL VALIDATOR] - VALIDATION FAILED             ║");
                Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝\n");
                Console.ResetColor();

                throw new InvalidDataException($"SQL Syntax Error: {errors.First().Message}");
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[SqlValidator] ✓ Syntax validation passed. SQL is well-formed.");
            Console.ResetColor();

            // ============================================================================
            // STEP 3: التحقق الأمني (Security Validation) - ENABLED!
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[SqlValidator] STEP 3: Performing security validation (Blacklist-Only)...");
            Console.ResetColor();

            // إنشاء الزائر الأمني (بدون Whitelist)
            var securityVisitor = new SecurityVisitor();

            Console.WriteLine($"[SqlValidator] Initiating SecurityVisitor to traverse SQL AST...");
            Console.WriteLine($"[SqlValidator] Checking for: DELETE, UPDATE, INSERT, DROP, ALTER, CREATE, EXEC, etc.");

            try
            {
                // التجول في شجرة بناء الجملة (AST)
                fragment.Accept(securityVisitor);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[SqlValidator] ✓ Security validation passed. No forbidden statements detected.");
                Console.ResetColor();
            }
            catch (Exception secEx)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[SqlValidator] ✗ SECURITY VALIDATION FAILED!");
                Console.WriteLine($"[SqlValidator] Security violation detected: {secEx.Message}");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║              [Layer 3: SQL VALIDATOR] - SECURITY VIOLATION            ║");
                Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝\n");
                Console.ResetColor();

                throw;
            }

            // ============================================================================
            // STEP 4: النجاح - الخروج
            // ============================================================================
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║           [Layer 3: SQL VALIDATOR] - VALIDATION SUCCESSFUL            ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝\n");
            Console.ResetColor();

            return Task.CompletedTask;
        }
    }
}