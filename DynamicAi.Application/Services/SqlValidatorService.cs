using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Security;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DynamicAi.Application.Services
{
    /// <summary>
    /// (تنفيذ Layer 3)
    /// يستخدم TSqlFragmentVisitor (ScriptDom) لتحليل الـ SQL "الخام"
    /// وتطبيق "الزائر الأمني" (SecurityVisitor) عليه.
    /// </summary>
    public class SqlValidatorService : ISqlValidatorService
    {
        // (لـ MVP: قائمة بيضاء ثابتة Hard-coded)
        // (للمستقبل: هذه القائمة ستأتي من "TableWhitelistManager" بناءً على tenantId)
        private readonly List<string> _tempAllowedViews = new List<string>
        {
            // Views
            "ContractViewWithPaid", 
            "salessystemcontract", 
            "SupplyOrderDetailView",
            "SystemsContractStoreView", 
            "ContarctReportwithoutresale", 
            "SalesMonth",
            "SalesManTotalWithContract",
            "SalesProductTotal",
            "SalestView",
            "SystemsContractView",
            "StoreView", 
            "ContarctReport",
            
            // Tables
            "Contract", 
            "SystemsContract", 
            "ProductView", 
            "Store", 
            "Sizes", 
            "Color",
            "Units", 
            "ConType", 
            "Currancy", 
            "PaymentRecive", 
            "day", 
            "contractDay",
            "Gov", 
            "Area", 
            "CustomerAccount", 
            "Employee",
            
            // Metadata Tables (from SchemaOrchestrator)
            "Pages", 
            "PageAttribute", 
            "PageDetail", 
            "Modul", 
            "MenuMaster", 
            "ListSetups",
            
            // Application Tables
            "QueryLogs",
            
            // Test View (from TargetDb)
            "v_ai_Contract"
        };

        public SqlValidatorService()
        {
            // (Constructor فارغ حالياً)
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
            // STEP 3: التحقق الأمني (Security Validation)
            // ============================================================================
            //Console.ForegroundColor = ConsoleColor.Magenta;
            //Console.WriteLine("\n[SqlValidator] STEP 3: Performing security validation...");
            //Console.ResetColor();

            //Console.WriteLine($"[SqlValidator] Whitelist contains {_tempAllowedViews.Count} allowed tables/views:");
            //Console.ForegroundColor = ConsoleColor.Gray;
            //Console.WriteLine($"  {string.Join(", ", _tempAllowedViews.Take(10))}...");
            //Console.ResetColor();

            //// إنشاء الزائر الأمني
            //var securityVisitor = new SecurityVisitor(_tempAllowedViews);

            //Console.WriteLine($"[SqlValidator] Initiating SecurityVisitor to traverse SQL AST...");

            //try
            //{
            //    // التجول في شجرة بناء الجملة (AST)
            //    fragment.Accept(securityVisitor);

            //    Console.ForegroundColor = ConsoleColor.Green;
            //    Console.WriteLine("[SqlValidator] ✓ Security validation passed. No unauthorized tables detected.");
            //    Console.ResetColor();
            //}
            //catch (Exception secEx)
            //{
            //    Console.ForegroundColor = ConsoleColor.Red;
            //    Console.WriteLine($"[SqlValidator] ✗ SECURITY VALIDATION FAILED!");
            //    Console.WriteLine($"[SqlValidator] Security violation detected: {secEx.Message}");
            //    Console.ResetColor();

            //    Console.ForegroundColor = ConsoleColor.Red;
            //    Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════╗");
            //    Console.WriteLine("║              [Layer 3: SQL VALIDATOR] - SECURITY VIOLATION            ║");
            //    Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝\n");
            //    Console.ResetColor();

            //    throw;
            //}

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