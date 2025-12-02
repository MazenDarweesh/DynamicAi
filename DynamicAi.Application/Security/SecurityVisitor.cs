using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.Collections.Generic;
using System.Security;

namespace DynamicAi.Application.Security
{
    /// <summary>
    /// (القلب الأمني للنظام - Layer 3)
    /// هذا "الزائر" (Visitor) يتجول في شجرة بناء الجملة (AST) للـ SQL
    /// ويفرض قواعد الأمان الصارمة التي اتفقنا عليها.
    /// </summary>
    public class SecurityVisitor : TSqlFragmentVisitor
    {
        /// <summary>
        /// (جديد) قائمة مؤقتة لتخزين أسماء الـ CTEs المعرفة داخل الكويري الحالي.
        /// </summary>
        private readonly HashSet<string> _definedCtes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// القائمة البيضاء (Whitelist) للجداول والـ Views المسموح بالاستعلام منها.
        /// (سيتم ملؤها من خدمة التحقق).
        /// </summary>
        private readonly HashSet<string> _allowedViews;

        /// <summary>
        /// (لـ MVP) قائمة سوداء (Blacklist) بسيطة للدوال الخطرة.
        /// </summary>
        private readonly HashSet<string> _blacklistedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EXEC", "EXECUTE", "SP_EXECUTESQL", "XP_CMDSHELL", "UNION"
            // (سنضيف المزيد هنا)
        };

        public SecurityVisitor(IEnumerable<string> allowedViews)
        {
            // (نحولها إلى HashSet لسرعة البحث)
            _allowedViews = new HashSet<string>(allowedViews, StringComparer.OrdinalIgnoreCase);
        }

        // --- 1. تسجيل الـ CTE عند تعريفه ---
        public override void Visit(CommonTableExpression node)
        {
            if (node.ExpressionName?.Value != null)
            {
                var cteName = node.ExpressionName.Value;
                _definedCtes.Add(cteName);

                // (Optional Debugging)
                // Console.WriteLine($"[SecurityVisitor] CTE Defined: {cteName}");
            }
            base.Visit(node);
        }
        // --- 2. التحقق عند استخدام أي جدول ---
        public override void Visit(NamedTableReference node)
        {
            if (node.SchemaObject?.BaseIdentifier?.Value != null)
            {
                var tableName = node.SchemaObject.BaseIdentifier.Value;

                // التحقق الثلاثي:
                // 1. هل هو جدول مسموح (Whitelist)؟
                bool isAllowedTable = _allowedViews.Contains(tableName);

                // 2. هل هو CTE تم تعريفه محلياً؟
                bool isDefinedCte = _definedCtes.Contains(tableName);

                // 3. (شبكة الأمان) هل يبدأ بالبادئة المخصصة للـ CTEs؟
                bool isGeneratedCte = tableName.StartsWith("CTE_Gen_", StringComparison.OrdinalIgnoreCase);

                if (!isAllowedTable && !isDefinedCte && !isGeneratedCte)
                {
                    // طباعة خطأ تفصيلي للمساعدة في الـ Debugging
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[SecurityVisitor] ⛔ Blocked Table: '{tableName}'");
                    Console.WriteLine($"  - In Whitelist? {isAllowedTable}");
                    Console.WriteLine($"  - In Local CTEs? {isDefinedCte}");
                    Console.WriteLine($"  - Has CTE Prefix? {isGeneratedCte}");
                    Console.ResetColor();

                    throw new SecurityException($"Security Validation Failed: Access to table/object '{tableName}' is not permitted. It is neither in the whitelist nor a valid local CTE.");
                }
            }
            base.Visit(node);
        }

        // --- 3. فرض القائمة السوداء (Blacklist) على الدوال ---
        public override void Visit(FunctionCall node)
        {
            if (node.FunctionName?.Value != null)
            {
                if (_blacklistedFunctions.Contains(node.FunctionName.Value))
                {
                    throw new SecurityException($"Security Validation Failed: Use of function '{node.FunctionName.Value}' is not allowed.");
                }
            }
            base.Visit(node);
        }

        // --- 1. منع أوامر الكتابة (DELETE, UPDATE, INSERT) ---

        public override void Visit(DeleteStatement node)
        {
            throw new SecurityException("Security Validation Failed: DELETE statements are not allowed.");
        }

        public override void Visit(UpdateStatement node)
        {
            throw new SecurityException("Security Validation Failed: UPDATE statements are not allowed.");
        }

        public override void Visit(InsertStatement node)
        {
            throw new SecurityException("Security Validation Failed: INSERT statements are not allowed.");
        }

        // (أضف المزيد للأوامر الخطرة مثل: Drop, Alter, Truncate, Create...)
        public override void Visit(DropTableStatement node)
        {
            throw new SecurityException("Security Validation Failed: DROP TABLE statements are not allowed.");
        }

        public override void Visit(AlterTableStatement node)
        {
            throw new SecurityException("Security Validation Failed: ALTER TABLE statements are not allowed.");
        }

        public override void Visit(TruncateTableStatement node)
        {
            throw new SecurityException("Security Validation Failed: TRUNCATE TABLE statements are not allowed.");
        }

        public override void Visit(CreateTableStatement node)
        {
            throw new SecurityException("Security Validation Failed: CREATE TABLE statements are not allowed.");
        }

        public override void Visit(DropIndexStatement node)
        {
            throw new SecurityException("Security Validation Failed: DROP INDEX statements are not allowed.");
        }

        public override void Visit(CreateIndexStatement node)
        {
            throw new SecurityException("Security Validation Failed: CREATE INDEX statements are not allowed.");
        }

        public override void Visit(DropViewStatement node)
        {
            throw new SecurityException("Security Validation Failed: DROP VIEW statements are not allowed.");
        }

        public override void Visit(CreateViewStatement node)
        {
            throw new SecurityException("Security Validation Failed: CREATE VIEW statements are not allowed.");
        }

        public override void Visit(DropProcedureStatement node)
        {
            throw new SecurityException("Security Validation Failed: DROP PROCEDURE statements are not allowed.");
        }

        public override void Visit(CreateProcedureStatement node)
        {
            throw new SecurityException("Security Validation Failed: CREATE PROCEDURE statements are not allowed.");
        }

       

    }
}