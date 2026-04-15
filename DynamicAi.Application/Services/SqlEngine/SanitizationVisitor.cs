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

        // [Error Grouping] Computed property — errors are consolidated at read time, not at add time.
        // This prevents token explosion in the Self-Healing loop (was: 1 error per column = N × full column list).
        public List<string> ValidationErrors => GenerateConsolidatedErrors();

        // Internal accumulator: TableName → (InvalidColumns, ValidColumns)
        private readonly Dictionary<string, (HashSet<string> InvalidCols, HashSet<string> ValidCols)>
            _invalidColumnsByTable = new Dictionary<string, (HashSet<string>, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);

        private string _currentClause = "UNKNOWN";

        // SELECT Aliases المعرّفة (مثل SUM(x) AS TotalSales)
        // نتجاهلها عند فحص ORDER BY / GROUP BY
        private readonly HashSet<string> _selectAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // قائمة بالكلمات المحجوزة والأجزاء المتعلقة بالتواريخ التي يعاملها البارسر كأعمدة بالخطأ
        private static readonly HashSet<string> IgnoredIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "day", "days", "dd", "d",
            "month", "months", "mm", "m",
            "year", "years", "yyyy", "yy",
            "week", "weeks", "wk", "ww",
            "hour", "hours", "hh",
            "minute", "minutes", "mi", "n",
            "second", "seconds", "ss", "s",
            "quarter", "qq", "q",
            "millisecond", "ms"
        };

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

            // التحقق من وجود الجدول في الـ Schema (Strict Mode)
            if (!_schemaMap.ContainsKey(realTableName))
            {
                var error = $"Table '{realTableName}' is not defined in the schema database.";
                Warnings.Add($"[Sanitizer] {error}");
                ValidationErrors.Add(error);
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
                // جمع الـ Aliases المعرّفة في SELECT (مثل: AS TotalSales)
                if (element is SelectScalarExpression scalarExpr && scalarExpr.ColumnName != null)
                {
                    _selectAliases.Add(scalarExpr.ColumnName.Value);
                }
                element.Accept(this);
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
            string? tableNameOrAlias = null;

            // تجاهل الكلمات المحفوظة أو أجزاء التواريخ (DateParts)
            if (identifiers.Count == 1 && IgnoredIdentifiers.Contains(columnName))
            {
                return;
            }

            // تجاهل SELECT Aliases في ORDER BY / GROUP BY
            // مثل: SUM(finaltotal) AS TotalSales ... ORDER BY TotalSales
            if (identifiers.Count == 1 && (_currentClause == "ORDER BY" || _currentClause == "GROUP BY")
                && _selectAliases.Contains(columnName))
            {
                return; // هذا alias معرّف في SELECT، مش column حقيقي
            }

            if (identifiers.Count > 1)
            {
                // الحالة الشائعة: Alias.ColumnName
                tableNameOrAlias = identifiers[identifiers.Count - 2].Value;
            }

            // 1. إذا لم نعرف الجدول (لا يوجد Alias)، هل هو موجود في أي من الجداول المعروفة؟
            // هذه حالة صعبة (Ambiguous)، لكن في الـ Sanitization، إذا لم نعرف المصدر، نفترض أنه صحيح (Innocent until proven guilty).
            // إلا إذا كان لدينا جدول واحد فقط في الـ FROM، وقتها يمكننا التحقق.
            string? targetRealTable = null;

            if (tableNameOrAlias != null)
            {
                // محاولة حل الـ Alias
                if (AliasMap.TryGetValue(tableNameOrAlias, out var mappedTable))
                {
                    targetRealTable = mappedTable;
                }
                else
                {
                    // الـ Alias نفسه غير معروف!
                    var error = $"Table alias '{tableNameOrAlias}' for column '{columnName}' is not defined in the FROM/JOIN clauses.";
                    Warnings.Add($"[Sanitizer] {error}");
                    ValidationErrors.Add(error);
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
                    // أكثر من جدول ولا يوجد Alias. خطأ صريح في الاستعلام المعقد
                    var error = $"Ambiguous column '{columnName}'. You must use an exact table alias (e.g. T1.{columnName}) when multiple tables are present.";
                    Warnings.Add($"[Sanitizer] {error}");
                    ValidationErrors.Add(error);
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
                    ColumnsToRemove.Add(node);

                    // [Error Grouping] Log a per-call warning (Diagnostic: visible in Logs per clause)
                    Warnings.Add($"[Sanitizer] Invalid column '{columnName}' found in '{targetRealTable}' (Clause: {_currentClause}).");

                    // [Error Grouping] Accumulate into dictionary instead of adding a full error string immediately.
                    // The consolidated error is generated only when ValidationErrors is read.
                    if (!_invalidColumnsByTable.TryGetValue(targetRealTable, out var entry))
                    {
                        entry = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), validColumns);
                        _invalidColumnsByTable[targetRealTable] = entry;
                    }
                    entry.InvalidCols.Add(columnName);
                }
            }
            // إذا الجدول نفسه غير موجود في الـ Schema (لم يظهر في الـ Check الأول)، نتجاوز.
        }

        // ============================================================================
        // [Error Grouping] Consolidator — runs at read time of ValidationErrors
        // Produces ONE error string per table instead of one per column.
        // Example output:
        //   Table 'Supplier' has 2 invalid column(s): [Name, Address].
        //   Available columns are: [accountid, bankaccount, ...]
        // ============================================================================
        private List<string> GenerateConsolidatedErrors()
        {
            var errors = new List<string>();

            foreach (var kvp in _invalidColumnsByTable)
            {
                var tableName   = kvp.Key;
                var invalidCols = kvp.Value.InvalidCols;
                var validCols   = kvp.Value.ValidCols;

                var invalidList  = string.Join(", ", invalidCols.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
                var availableStr = string.Join(", ", validCols.Take(30))
                                  + (validCols.Count > 30 ? "..." : "");

                errors.Add(
                    $"Table '{tableName}' has {invalidCols.Count} invalid column(s): [{invalidList}]. " +
                    $"Available columns are: [{availableStr}]"
                );
            }

            return errors;
        }
    }
}
