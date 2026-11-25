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

        private readonly List<string> _otherValidationErrors = new List<string>();
        private readonly HashSet<string> _otherValidationErrorSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Lexical scope stacks for nested QuerySpecifications (subqueries / CTEs / derived tables)
        private readonly Stack<Dictionary<string, string>> _aliasScopes = new();
        private readonly Stack<HashSet<string>> _selectAliasScopes = new();
        private readonly Stack<HashSet<string>> _derivedAliasScopes = new();
        private readonly Stack<HashSet<string>> _cteScopes = new();
        private readonly HashSet<string> _fallbackCteNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _fallbackDerivedAliases = new(StringComparer.OrdinalIgnoreCase);

        // Internal accumulator: TableName → (InvalidColumns, ValidColumns)
        private readonly Dictionary<string, (HashSet<string> InvalidCols, HashSet<string> ValidCols)>
            _invalidColumnsByTable = new Dictionary<string, (HashSet<string>, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);

        private string _currentClause = "UNKNOWN";

        // SELECT Aliases المعرّفة (مثل SUM(x) AS TotalSales)
        // نتجاهلها عند فحص ORDER BY / GROUP BY (fallback when outside QuerySpecification)
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
        // PHASE 1: Alias Mapping (FROM / JOIN / CTEs / Derived Tables)
        // ============================================================================

        public override void ExplicitVisit(SelectStatement node)
        {
            var cteScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (node.WithCtesAndXmlNamespaces?.CommonTableExpressions != null)
            {
                foreach (var cte in node.WithCtesAndXmlNamespaces.CommonTableExpressions)
                {
                    if (!string.IsNullOrWhiteSpace(cte.ExpressionName?.Value))
                    {
                        cteScope.Add(cte.ExpressionName.Value);
                    }
                }
            }

            _cteScopes.Push(cteScope);
            try
            {
                Visit(node);
                node.AcceptChildren(this);
            }
            finally
            {
                _cteScopes.Pop();
            }
        }

        public override void Visit(CommonTableExpression node)
        {
            var cteName = node.ExpressionName?.Value;
            if (!string.IsNullOrWhiteSpace(cteName))
            {
                if (_cteScopes.Count > 0)
                {
                    _cteScopes.Peek().Add(cteName);
                }
                else
                {
                    _fallbackCteNames.Add(cteName);
                }
            }

            base.Visit(node);
        }

        public override void Visit(QueryDerivedTable node)
        {
            var alias = node.Alias?.Value;
            if (!string.IsNullOrWhiteSpace(alias))
            {
                RegisterAlias(alias, alias);
                if (_derivedAliasScopes.Count > 0)
                {
                    _derivedAliasScopes.Peek().Add(alias);
                }
                else
                {
                    _fallbackDerivedAliases.Add(alias);
                }
            }

            base.Visit(node);
        }

        public override void Visit(NamedTableReference node)
        {
            _currentClause = "FROM/JOIN";
            // استخراج اسم الجدول الحقيقي
            string? realTableName = node.SchemaObject?.BaseIdentifier?.Value;
            if (string.IsNullOrWhiteSpace(realTableName))
            {
                return;
            }

            bool isCte = IsCteName(realTableName);

            // التحقق من وجود الجدول في الـ Schema (Strict Mode)
            if (!isCte && !_schemaMap.ContainsKey(realTableName))
            {
                var error = $"Table '{realTableName}' is not defined in the schema database.";
                AddOtherValidationError(error);
                return;
            }

            // تسجيل الـ Alias إذا وجد، وإلا فالاسم نفسه هو الـ key
            string aliasKey = node.Alias != null ? node.Alias.Value : realTableName;
            RegisterAlias(aliasKey, realTableName);

            base.Visit(node);
        }

        // ============================================================================
        // PHASE 2: Column Validation (SELECT / WHERE / GROUP BY / HAVING / ORDER BY)
        // ============================================================================

        public override void ExplicitVisit(QuerySpecification node)
        {
            var previousClause = _currentClause;
            _aliasScopes.Push(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            _selectAliasScopes.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            _derivedAliasScopes.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            try
            {
                // زيارة الـ FROM أولاً لبناء الـ Aliases (مهم جداً)
                if (node.FromClause != null)
                {
                    _currentClause = "FROM/JOIN";
                    node.FromClause.Accept(this);
                }

                // ثم زيارة الـ SELECT Elements للتحقق من الأعمدة
                _currentClause = "SELECT";
                var currentSelectAliases = _selectAliasScopes.Peek();
                foreach (var element in node.SelectElements)
                {
                    // جمع الـ Aliases المعرّفة في SELECT (مثل: AS TotalSales)
                    if (element is SelectScalarExpression scalarExpr && scalarExpr.ColumnName != null)
                    {
                        currentSelectAliases.Add(scalarExpr.ColumnName.Value);
                    }
                    element.Accept(this);
                }

                if (node.TopRowFilter != null)
                {
                    _currentClause = "TOP";
                    node.TopRowFilter.Accept(this);
                }

                // زيارة باقي الأجزاء (Where, GroupBy, OrderBy, etc.) للبحث عن الأعمدة غير الصالحة فيها أيضاً
                if (node.WhereClause != null)
                {
                    _currentClause = "WHERE";
                    node.WhereClause.Accept(this);
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

                if (node.OrderByClause != null)
                {
                    _currentClause = "ORDER BY";
                    node.OrderByClause.Accept(this);
                }

                if (node.OffsetClause != null)
                {
                    _currentClause = "OFFSET";
                    node.OffsetClause.Accept(this);
                }
            }
            finally
            {
                _aliasScopes.Pop();
                _selectAliasScopes.Pop();
                _derivedAliasScopes.Pop();
                _currentClause = previousClause;
            }
        }

        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.ColumnType == ColumnType.Wildcard ||
                node.MultiPartIdentifier == null ||
                node.MultiPartIdentifier.Identifiers.Count == 0)
            {
                return;
            }

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
            var currentSelectAliases = _selectAliasScopes.Count > 0
                ? _selectAliasScopes.Peek()
                : _selectAliases;

            if (identifiers.Count == 1 && (_currentClause == "ORDER BY" || _currentClause == "GROUP BY")
                && currentSelectAliases.Contains(columnName))
            {
                return; // هذا alias معرّف في SELECT، مش column حقيقي
            }

            if (identifiers.Count > 1)
            {
                // الحالة الشائعة: Alias.ColumnName
                tableNameOrAlias = identifiers[identifiers.Count - 2].Value;
            }

            string? targetRealTable;
            bool isDerivedAlias;

            if (tableNameOrAlias != null)
            {
                // حل الـ Alias من النطاق الداخلي إلى الخارجي (يدعم Correlated Subqueries)
                targetRealTable = ResolveQualifiedTable(tableNameOrAlias, out isDerivedAlias);
                if (targetRealTable == null)
                {
                    var error = $"Table alias '{tableNameOrAlias}' for column '{columnName}' is not defined in the FROM/JOIN clauses.";
                    AddOtherValidationError(error);
                    return;
                }
            }
            else
            {
                // لا يوجد Alias: التحقق ضمن النطاق الحالي (أو النطاقات المحيطة إذا كان النطاق الحالي فارغاً)
                targetRealTable = ResolveUnqualifiedTable(columnName, out isDerivedAlias);
                if (targetRealTable == null)
                {
                    return;
                }
            }

            if (isDerivedAlias || IsCteName(targetRealTable))
            {
                return;
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

        private void RegisterAlias(string aliasKey, string realTableName)
        {
            if (_aliasScopes.Count > 0)
            {
                _aliasScopes.Peek()[aliasKey] = realTableName;
            }

            if (_aliasScopes.Count <= 1)
            {
                AliasMap[aliasKey] = realTableName;
            }
            else
            {
                AliasMap.TryAdd(aliasKey, realTableName);
            }
        }

        private bool IsCteName(string tableName)
        {
            foreach (var cteScope in _cteScopes)
            {
                if (cteScope.Contains(tableName))
                {
                    return true;
                }
            }
            return _fallbackCteNames.Contains(tableName);
        }

        private string? ResolveQualifiedTable(string tableNameOrAlias, out bool isDerivedAlias)
        {
            if (_aliasScopes.Count > 0)
            {
                foreach (var (aliasScope, derivedScope) in _aliasScopes.Zip(_derivedAliasScopes))
                {
                    if (aliasScope.TryGetValue(tableNameOrAlias, out var mappedTable))
                    {
                        isDerivedAlias = derivedScope.Contains(tableNameOrAlias);
                        return mappedTable;
                    }
                }
            }
            else if (AliasMap.TryGetValue(tableNameOrAlias, out var fallbackTable))
            {
                isDerivedAlias = _fallbackDerivedAliases.Contains(tableNameOrAlias);
                return fallbackTable;
            }

            isDerivedAlias = false;
            return null;
        }

        private string? ResolveUnqualifiedTable(string columnName, out bool isDerivedAlias)
        {
            if (_aliasScopes.Count > 0)
            {
                foreach (var (scope, derivedScope) in _aliasScopes.Zip(_derivedAliasScopes))
                {
                    if (scope.Count == 1)
                    {
                        var singleEntry = scope.First();
                        isDerivedAlias = derivedScope.Contains(singleEntry.Key);
                        return singleEntry.Value;
                    }

                    if (scope.Count > 1)
                    {
                        var error = $"Ambiguous column '{columnName}'. You must use an exact table alias (e.g. T1.{columnName}) when multiple tables are present.";
                        AddOtherValidationError(error);
                        isDerivedAlias = false;
                        return null;
                    }
                }

                isDerivedAlias = false;
                return null;
            }

            if (AliasMap.Count == 1)
            {
                var singleEntry = AliasMap.First();
                isDerivedAlias = _fallbackDerivedAliases.Contains(singleEntry.Key);
                return singleEntry.Value;
            }

            if (AliasMap.Count > 1)
            {
                var error = $"Ambiguous column '{columnName}'. You must use an exact table alias (e.g. T1.{columnName}) when multiple tables are present.";
                AddOtherValidationError(error);
            }

            isDerivedAlias = false;
            return null;
        }

        private void AddOtherValidationError(string error)
        {
            if (_otherValidationErrorSet.Add(error))
            {
                Warnings.Add($"[Sanitizer] {error}");
                _otherValidationErrors.Add(error);
            }
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
            var errors = new List<string>(_otherValidationErrors);

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
