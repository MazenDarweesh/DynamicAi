using DynamicAi.Application.Models.DirectIntegration;
using Microsoft.SqlServer.TransactSql.ScriptDom;

/// <summary>
/// (الزائر "الحارس" الذي يقوم بالتحصين - Layer 4.8)
///
/// مسؤوليات هذا الزائر:
///   1. حقن TOP للحد من حجم النتائج.
///   2. التحقق من وجود كل TailCondition المرتبطة بكل مصدر بياني (View/Table).
///      • إذا كان الـ LLM قد أضاف الشرط بالفعل → لا يُعدّل شيء (Trust LLM).
///      • إذا كان الشرط ناقصاً → يُحقن في الـ QuerySpecification الصحيح
///        الذي يحتوي على الـ View صاحب الشرط.
///
/// تغيير معماري مهم: لم نعد نحقن جميع الشروط في أعلى QuerySpecification بشكل أعمى.
/// كل شرط مرتبط بـ ViewName + SqlAlias، ويُبحث عنه في الـ FromClause الصحيح.
/// </summary>
internal class SqlHardeningVisitor : TSqlFragmentVisitor
{
    public Dictionary<string, object> Parameters { get; private set; }
    private readonly string _tenantId;
    private readonly int _topRows;
    private readonly List<TailConditionEntry> _tailConditions;

    // ─── Injection tracking ────────────────────────────────────────────────
    // Maps ViewName (lower-invariant) → whether its condition was already found/injected.
    private readonly Dictionary<string, bool> _conditionApplied;

    // Populated during ExplicitVisit(NamedTableReference) so injection knows
    // which QuerySpecification owns which table reference.
    private readonly Dictionary<QuerySpecification, HashSet<string>> _queryToViews = new();
    private readonly Stack<QuerySpecification> _queryStack = new();

    // TOP already injected guard (only once for outermost query)
    private bool _topInjected = false;

    public SqlHardeningVisitor(
        Dictionary<string, object> parameters,
        string tenantId,
        int topRows,
        IEnumerable<TailConditionEntry>? tailConditions = null)
    {
        Parameters = parameters;
        _tenantId = tenantId;
        _topRows = topRows;
        _tailConditions = tailConditions?
            .Where(e => !string.IsNullOrWhiteSpace(e.Condition) && !string.IsNullOrWhiteSpace(e.ViewName))
            .ToList() ?? new List<TailConditionEntry>();

        _conditionApplied = _tailConditions
            .ToDictionary(e => e.ViewName.ToLowerInvariant(), _ => false);
    }

    // ─── Pass 1: Build QuerySpecification → Views map ─────────────────────

    // ─── REMOVED: Visit(QuerySpecification) — it fired BEFORE ExplicitVisit causing
    //             _queryStack to receive the same node TWICE (pushed) but only popped
    //             ONCE at the end of ExplicitVisit, corrupting scope tracking.
    //             TOP injection is now handled exclusively inside ExplicitVisit.

    public override void ExplicitVisit(QuerySpecification node)
    {
        _queryStack.Push(node);

        if (!_queryToViews.ContainsKey(node))
            _queryToViews[node] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // TOP Injection (outermost query only — moved here from Visit)
        if (!_topInjected && node.TopRowFilter == null && node.OffsetClause == null)
        {
            node.TopRowFilter = new TopRowFilter
            {
                Expression = new IntegerLiteral { Value = _topRows.ToString() }
            };
            _topInjected = true;
        }

        // Visit children so named table references register themselves
        node.AcceptChildren(this);

        // ─── After children visited: determine which tail conditions are needed here ───
        if (_tailConditions.Any())
        {
            ApplyMissingConditionsToQuery(node);
        }

        _queryStack.TryPop(out _);
    }

    /// <summary>
    /// When the visitor encounters a table reference (FROM or JOIN), register it
    /// under the nearest enclosing QuerySpecification.
    /// </summary>
    public override void ExplicitVisit(NamedTableReference node)
    {
        if (_queryStack.TryPeek(out var currentQuery))
        {
            var tableName = node.SchemaObject?.BaseIdentifier?.Value ?? string.Empty;
            if (!string.IsNullOrEmpty(tableName))
                _queryToViews[currentQuery].Add(tableName);

            // Also register the alias if present
            var alias = node.Alias?.Value ?? string.Empty;
            if (!string.IsNullOrEmpty(alias))
                _queryToViews[currentQuery].Add(alias);
        }
    }

    // ─── Per-query condition injection ────────────────────────────────────

    private void ApplyMissingConditionsToQuery(QuerySpecification queryNode)
    {
        var viewsInQuery = _queryToViews.TryGetValue(queryNode, out var v) ? v : new HashSet<string>();

        foreach (var entry in _tailConditions)
        {
            var key = entry.ViewName.ToLowerInvariant();

            // Only act on conditions whose view appears in this query scope
            bool viewPresent = viewsInQuery.Contains(entry.ViewName)
                            || viewsInQuery.Contains(entry.SqlAlias);

            if (!viewPresent) continue;

            // Check if condition text already exists in the WHERE clause text
            bool alreadyPresent = IsConditionAlreadyInWhere(queryNode, entry.Condition);

            if (alreadyPresent)
            {
                _conditionApplied[key] = true;
                continue;
            }

            // Condition is MISSING → inject it (safety net)
            InjectCondition(queryNode, entry);
            _conditionApplied[key] = true;
        }
    }

    /// <summary>
    /// Smart check: extracts business tokens (columns/tables) from the condition 
    /// and checks if they exist in the LLM-generated WHERE clause. 
    /// This avoids false-positive injections even if the LLM cleans up '1=1' or uses aliases.
    /// </summary>
    private static bool IsConditionAlreadyInWhere(QuerySpecification node, string condition)
    {
        if (node.WhereClause == null) return false;

        var gen = new Sql160ScriptGenerator();
        gen.GenerateScript(node.WhereClause, out var whereText);

        var tokens = ExtractBusinessTokens(condition);
        if (!tokens.Any()) return false;

        foreach (var token in tokens)
        {
            if (!whereText.Contains(token, StringComparison.OrdinalIgnoreCase))
                return false; // The LLM missed a crucial column/table, so we must inject.
        }

        return true; // The LLM successfully included all business tokens.
    }

    /// <summary>
    /// Extracts potential column and table names from the condition string.
    /// It splits by non-alphanumeric chars, and filters out SQL keywords and short strings.
    /// </summary>
    private static IEnumerable<string> ExtractBusinessTokens(string condition)
    {
        // SQL keywords to ignore when extracting tokens
        var ignoredKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "FROM", "WHERE", "AND", "OR", "IN", "IS", "NOT", "NULL", "EXISTS", 
            "LIKE", "CAST", "CONVERT", "BETWEEN", "AS", "ON", "JOIN", "INNER", "LEFT", "RIGHT", "OUTER"
        };

        // Split by non-alphanumeric characters (spaces, parenthesis, commas, etc.)
        var words = System.Text.RegularExpressions.Regex.Split(condition, @"[^a-zA-Z0-9_]+");

        return words
            .Where(w => w.Length > 2) // Ignore small words like 'T1', 'Id' to avoid false positives
            .Where(w => !ignoredKeywords.Contains(w)) // Ignore SQL keywords
            .Where(w => !int.TryParse(w, out _)) // Ignore numbers
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Parses the condition string and AND-appends it to the query's WHERE clause.
    /// The condition may already include alias prefixes (the LLM adds them). We
    /// inject it verbatim as provided by the business configuration.
    /// </summary>
    private static void InjectCondition(QuerySpecification queryNode, TailConditionEntry entry)
    {
        string cleanedTail = entry.Condition.Trim();
        // (Removal of WHERE is now handled at the source in UnifiedQueryHandler, but keeping as safety)
        if (cleanedTail.StartsWith("WHERE", StringComparison.OrdinalIgnoreCase))
            cleanedTail = cleanedTail[5..].Trim();

        var parser = new TSql160Parser(false);
        using var reader = new StringReader($"SELECT 1 WHERE ({cleanedTail})");
        var fragment = parser.Parse(reader, out var errors);

        if (errors.Count > 0 || fragment is not TSqlScript script || !script.Batches.Any())
            return; // Unparseable condition — skip injection rather than break SQL

        var statement = script.Batches[0].Statements[0] as SelectStatement;
        var tailExpr = (statement?.QueryExpression as QuerySpecification)?.WhereClause?.SearchCondition;

        if (tailExpr == null) return;

        // ─── ALIAS INJECTION (The Safety Net's Safety Net) ───
        // If the LLM or the config metadata forgot aliases, we add them here.
        // We only target columns in the current scope, skipping subqueries.
        var aliasInjector = new AliasInjectorVisitor(entry.SqlAlias);
        tailExpr.Accept(aliasInjector);

        if (queryNode.WhereClause == null)
        {
            queryNode.WhereClause = new WhereClause { SearchCondition = tailExpr };
        }
        else
        {
            queryNode.WhereClause.SearchCondition = new BooleanBinaryExpression
            {
                FirstExpression = new BooleanParenthesisExpression { Expression = queryNode.WhereClause.SearchCondition },
                SecondExpression = new BooleanParenthesisExpression { Expression = tailExpr },
                BinaryExpressionType = BooleanBinaryExpressionType.And
            };
        }
    }

    // ─── Legacy Visit overrides (kept for compat) ─────────────────────────
    public override void Visit(StringLiteral node) { /* Parameter binding deferred to Dapper */ }
    public override void Visit(FromClause node) { /* Tenant isolation handled by the views */ }

    /// <summary>
    /// زائر يقوم بحقن الـ Alias في الأعمدة التي تفتقر إليه.
    /// يتجاهل الـ Subqueries لضمان عدم إفساد الأعمدة الداخلية فيها.
    /// </summary>
    private class AliasInjectorVisitor : TSqlFragmentVisitor
    {
        private readonly string _alias;

        public AliasInjectorVisitor(string alias)
        {
            _alias = alias;
        }

        // 1. Stop traversal into subqueries (IN/EXISTS subqueries inside the condition).
        //    CRITICAL: Must override ExplicitVisit — NOT Visit.
        //    In ScriptDom, Visit fires but AcceptChildren is called from ExplicitVisit.
        //    Overriding Visit with an empty body does NOT stop AcceptChildren from firing.
        //    By overriding ExplicitVisit without calling base, we prevent child traversal entirely.
        public override void ExplicitVisit(QuerySpecification node)
        {
            // Intentionally empty — do not descend into subqueries.
            // This ensures T1 prefix is NOT injected into inner subquery columns
            // e.g. DepartmentId inside: SELECT DepartmentId FROM EmpployeeDepartments
        }

        // 2. Inject the alias into unaliased columns in the OUTER scope only.
        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier != null && node.MultiPartIdentifier.Identifiers.Count == 1)
            {
                // Column has no alias prefix (e.g. DepartmentId) → prepend it (e.g. T1.DepartmentId)
                node.MultiPartIdentifier.Identifiers.Insert(0, new Identifier { Value = _alias });
            }
            // NOTE: base.Visit is intentionally NOT called — ColumnReferenceExpression has no children to recurse.
        }
    }
}
