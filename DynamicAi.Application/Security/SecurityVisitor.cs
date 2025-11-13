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

        // --- 2. فرض القائمة البيضاء (Whitelist) على الجداول ---

        public override void Visit(NamedTableReference node)
        {
            // (هذا هو المكان الذي يتحقق فيه من أسماء الجداول في جملة FROM/JOIN)
            if (node.SchemaObject?.BaseIdentifier?.Value != null)
            {
                var tableName = node.SchemaObject.BaseIdentifier.Value;

                // (الأهم) التحقق من القائمة البيضاء
                if (!_allowedViews.Contains(tableName))
                {
                    throw new SecurityException($"Security Validation Failed: Access to table '{tableName}' is not permitted.");
                }
            }
            base.Visit(node); // (استمر في زيارة باقي الأجزاء)
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
    }
}