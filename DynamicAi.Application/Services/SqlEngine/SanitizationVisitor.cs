using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DynamicAi.Application.Services.SqlEngine
{
    /// <summary>
    /// زائر مزدوج الغرض (Dual-Purpose Visitor):
    /// 1. يبني خريطة الـ Aliases (لربط كل اسم مختصر بالجدول الحقيقي).
    /// 2. يجمع الأعمدة غير الصالحة (Invalid Columns) التي يجب حذفها.
    /// </summary>
    public class SanitizationVisitor : TSqlFragmentVisitor
    {
        private readonly Dictionary<string, HashSet<string>> _schemaMap;
        
        // خريطة الـ Aliases: 
        // Key = Alias (e.g., "u")
        // Value = Real Table Name (e.g., "users")
        public Dictionary<string, string> AliasMap { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // قائمة الأعمدة المراد حذفها (نخزن الـ Node نفسه)
        public List<ColumnReferenceExpression> ColumnsToRemove { get; } = new List<ColumnReferenceExpression>();

        public List<string> Warnings { get; } = new List<string>();

        // حالة الزيارة الحالية
        private bool _isInSelectClause = false;
        private string _currentClause = "UNKNOWN";

        public SanitizationVisitor(Dictionary<string, HashSet<string>> schemaMap)
        {
            _schemaMap = schemaMap;
        }

        // ============================================================================
        // PHASE 1: Alias Mapping (FROM / JOIN)
        // ============================================================================

        public override void Visit(NamedTableReference node)
        {
            _currentClause = "FROM/JOIN";
            // استخراج اسم الجدول الحقيقي
            string realTableName = node.SchemaObject.BaseIdentifier.Value;

            // التحقق من وجود الجدول في الـ Schema (للعلم فقط)
            if (!_schemaMap.ContainsKey(realTableName))
            {
                Warnings.Add($"[Sanitizer] Table '{realTableName}' not found in schema. Skipping validation for its columns.");
                // لا نحذفه حسب طلب المستخدم، لكن نسجله عشان نتجاهل أعمدته
                return;
            }

            // تسجيل الـ Alias إذا وجد
            if (node.Alias != null)
            {
                AliasMap[node.Alias.Value] = realTableName;
            }
            else
            {
                // إذا لم يكن هناك Alias، فالاسم نفسه هو الـ key
                AliasMap[realTableName] = realTableName;
            }

            base.Visit(node);
        }

        // ============================================================================
        // PHASE 2: Column Validation (SELECT)
        // ============================================================================

        public override void Visit(QuerySpecification node)
        {
            // نحن هنا ندخل داخل جملة SELECT ... FROM ...
            // عملية الـ Visit للأبناء ستتم تلقائياً، لكن نحتاج تحديد أننا في الـ Select Elements
            
            // زيارة الـ FROM أولاً لبناء الـ Aliases (مهم جداً)
            if (node.FromClause != null)
            {
                node.FromClause.Accept(this);
            }

            // ثم زيارة الـ SELECT Elements للتحقق من الأعمدة
            _currentClause = "SELECT";
            foreach (var element in node.SelectElements)
            {
                // نفعل هذا Flag عشان لما نوصل لـ ColumnReferenceExpression نعرف إننا في الـ Select list
                _isInSelectClause = true;
                element.Accept(this);
                _isInSelectClause = false;
            }

            // زيارة باقي الأجزاء (Where, GroupBy, OrderBy, etc.) للبحث عن الأعمدة غير الصالحة فيها أيضاً
            if (node.WhereClause != null) 
            {
                _currentClause = "WHERE";
                node.WhereClause.Accept(this);
            }

            if (node.OrderByClause != null) 
            {
                _currentClause = "ORDER BY";
                node.OrderByClause.Accept(this);
            }

            if (node.GroupByClause != null) 
            {
                _currentClause = "GROUP BY";
                node.GroupByClause.Accept(this);
            }

            if (node.HavingClause != null) 
            {
                _currentClause = "HAVING";
                node.HavingClause.Accept(this);
            }
        }

        public override void Visit(ColumnReferenceExpression node)
        {
            // سابقاً كنا نتحقق فقط من جملة SELECT، ولكن إذا تُرك عمود خاطئ في WHERE أو GROUP BY 
            // سيفشل الاستعلام بأكمله في SQL Server. لذا يجب فحصه في كل مكان.

            // استخراج اسم العمود
            // MultiPartIdentifier قد يكون:
            // - ColName (Length 1)
            // - Alias.ColName (Length 2)
            // - Schema.Table.ColName (Length 3)
            
            var identifiers = node.MultiPartIdentifier.Identifiers;
            string columnName = identifiers.Last().Value; // دائماً الأخير هو اسم العمود
            string tableNameOrAlias = null;

            if (identifiers.Count > 1)
            {
                // الحالة الشائعة: Alias.ColumnName
                tableNameOrAlias = identifiers[identifiers.Count - 2].Value;
            }

            // 1. إذا لم نعرف الجدول (لا يوجد Alias)، هل هو موجود في أي من الجداول المعروفة؟
            // هذه حالة صعبة (Ambiguous)، لكن في الـ Sanitization، إذا لم نعرف المصدر، نفترض أنه صحيح (Innocent until proven guilty).
            // إلا إذا كان لدينا جدول واحد فقط في الـ FROM، وقتها يمكننا التحقق.
            string targetRealTable = null;

            if (tableNameOrAlias != null)
            {
                // محاولة حل الـ Alias
                if (AliasMap.TryGetValue(tableNameOrAlias, out var mappedTable))
                {
                    targetRealTable = mappedTable;
                }
                else
                {
                    // الـ Alias نفسه غير معروف! هل نعتبره خطأ؟
                    // ربما هو جدول غير موجود، وبما أننا لا نحذف الجداول، فلن نحذف أعمدتها (لأننا لا نملك Schema لها).
                    return; 
                }
            }
            else
            {
                // لا يوجد Alias. 
                // إذا كان لدينا جدول واحد فقط في الخريطة، نستخدمه.
                if (AliasMap.Count == 1)
                {
                    targetRealTable = AliasMap.Values.First();
                }
                else
                {
                    // أكثر من جدول ولا يوجد Alias. لا يمكننا التحقق بدقة. نتجاوز.
                    return;
                }
            }

            // الآن لدينا: targetRealTable + columnName
            // نتحقق من الـ Schema
            if (_schemaMap.TryGetValue(targetRealTable, out var validColumns))
            {
                // هل العمود موجود؟
                if (columnName != "*" && !validColumns.Contains(columnName))
                {
                    // 🚨 العمود غير موجود!
                    // هذا هو الهدف: Mark for Deletion
                    ColumnsToRemove.Add(node);
                    Warnings.Add($"[Sanitizer] Invalid column '{columnName}' found in '{targetRealTable}' (Clause: {_currentClause}). Scheduled for removal.");
                }
            }
            // إذا الجدول نفسه غير موجود في الـ Schema (لم يظهر في الـ Check الأول)، نتجاوز.
        }
    }
}
