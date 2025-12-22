using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.Collections.Generic;
using System.Security;

namespace DynamicAi.Application.Security
{
    /// <summary>
    /// (القلب الأمني للنظام - Layer 3)
    /// هذا "الزائر" (Visitor) يتجول في شجرة بناء الجملة (AST) للـ SQL
    /// ويفرض قواعد الأمان الصارمة.
    /// 
    /// النهج المتبع: Blacklist-Only
    /// - لا نستخدم Whitelist للجداول (الـ AI مقيد بالـ Prompt)
    /// - نحظر أوامر DDL/DML الخطرة فقط
    /// - الـ CTEs معطلة في الـ Prompt (NO CTEs ALLOWED rule)
    /// </summary>
    public class SecurityVisitor : TSqlFragmentVisitor
    {
        /// <summary>
        /// (لـ MVP) قائمة سوداء (Blacklist) للدوال والأوامر الخطرة.
        /// يمكن إضافة أسماء جداول محظورة هنا لاحقاً إذا لزم الأمر.
        /// </summary>
        private readonly HashSet<string> _blacklistedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // (فارغة حالياً - يمكن إضافة جداول نظام محظورة لاحقاً)
            // مثال: "sys.objects", "INFORMATION_SCHEMA.TABLES"
        };

        /// <summary>
        /// قائمة سوداء للدوال الخطرة.
        /// </summary>
        private readonly HashSet<string> _blacklistedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EXEC", "EXECUTE", "SP_EXECUTESQL", "XP_CMDSHELL", "OPENROWSET", "OPENDATASOURCE"
        };

        public SecurityVisitor()
        {
            // Constructor بسيط - لا نحتاج Whitelist
        }

        // ============================================================================
        // CTE Handling - COMMENTED OUT (CTEs are forbidden in prompt)
        // ============================================================================
        // private readonly HashSet<string> _definedCtes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 
        // public override void Visit(CommonTableExpression node)
        // {
        //     if (node.ExpressionName?.Value != null)
        //     {
        //         var cteName = node.ExpressionName.Value;
        //         _definedCtes.Add(cteName);
        //     }
        //     base.Visit(node);
        // }

        // ============================================================================
        // Blacklist Table Validation (Optional - for future use)
        // ============================================================================
        public override void Visit(NamedTableReference node)
        {
            if (node.SchemaObject?.BaseIdentifier?.Value != null)
            {
                var tableName = node.SchemaObject.BaseIdentifier.Value;

                // التحقق من القائمة السوداء فقط
                if (_blacklistedTables.Contains(tableName))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[SecurityVisitor] ⛔ Blocked Table: '{tableName}' (Blacklisted)");
                    Console.ResetColor();

                    throw new SecurityException($"Security Validation Failed: Access to table '{tableName}' is explicitly forbidden.");
                }
            }
            base.Visit(node);
        }

        // ============================================================================
        // Blacklist Function Validation
        // ============================================================================
        public override void Visit(FunctionCall node)
        {
            if (node.FunctionName?.Value != null)
            {
                if (_blacklistedFunctions.Contains(node.FunctionName.Value))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[SecurityVisitor] ⛔ Blocked Function: '{node.FunctionName.Value}'");
                    Console.ResetColor();

                    throw new SecurityException($"Security Validation Failed: Use of function '{node.FunctionName.Value}' is not allowed.");
                }
            }
            base.Visit(node);
        }

        // ============================================================================
        // DDL/DML Blocking - The Core Security Layer
        // ============================================================================

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

        // ============================================================================
        // Additional DDL Blocking
        // ============================================================================

        public override void Visit(DropFunctionStatement node)
        {
            throw new SecurityException("Security Validation Failed: DROP FUNCTION statements are not allowed.");
        }

        public override void Visit(CreateFunctionStatement node)
        {
            throw new SecurityException("Security Validation Failed: CREATE FUNCTION statements are not allowed.");
        }

        public override void Visit(DropTriggerStatement node)
        {
            throw new SecurityException("Security Validation Failed: DROP TRIGGER statements are not allowed.");
        }

        public override void Visit(CreateTriggerStatement node)
        {
            throw new SecurityException("Security Validation Failed: CREATE TRIGGER statements are not allowed.");
        }

        public override void Visit(GrantStatement node)
        {
            throw new SecurityException("Security Validation Failed: GRANT statements are not allowed.");
        }

        public override void Visit(DenyStatement node)
        {
            throw new SecurityException("Security Validation Failed: DENY statements are not allowed.");
        }

        public override void Visit(RevokeStatement node)
        {
            throw new SecurityException("Security Validation Failed: REVOKE statements are not allowed.");
        }
    }
}