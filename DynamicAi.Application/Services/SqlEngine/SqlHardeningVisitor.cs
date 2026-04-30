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

    public override void Visit(QuerySpecification node)
    {
        _queryStack.Push(node);

        if (!_queryToViews.ContainsKey(node))
            _queryToViews[node] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // TOP Injection (outermost query only)
        if (!_topInjected && node.TopRowFilter == null && node.OffsetClause == null)
        {
            node.TopRowFilter = new TopRowFilter
            {
                Expression = new IntegerLiteral { Value = _topRows.ToString() }
            };
            _topInjected = true;
        }

        base.Visit(node);
    }

    public override void ExplicitVisit(QuerySpecification node)
    {
        _queryStack.Push(node);

        if (!_queryToViews.ContainsKey(node))
            _queryToViews[node] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
    /// Rough check: serialize the WHERE clause to string and look for the
    /// core token of the condition. This avoids false-positive injections.
    /// </summary>
    private static bool IsConditionAlreadyInWhere(QuerySpecification node, string condition)
    {
        if (node.WhereClause == null) return false;

        // Extract a short key from the condition to search for (first 15 non-space chars)
        var key = new string(condition.Where(c => !char.IsWhiteSpace(c)).Take(15).ToArray());
        if (string.IsNullOrEmpty(key)) return false;

        var gen = new Sql160ScriptGenerator();
        gen.GenerateScript(node.WhereClause, out var whereText);
        return whereText.Contains(key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses the condition string and AND-appends it to the query's WHERE clause.
    /// The condition may already include alias prefixes (the LLM adds them). We
    /// inject it verbatim as provided by the business configuration.
    /// </summary>
    private static void InjectCondition(QuerySpecification queryNode, TailConditionEntry entry)
    {
        string cleanedTail = entry.Condition.Trim();
        if (cleanedTail.StartsWith("WHERE", StringComparison.OrdinalIgnoreCase))
            cleanedTail = cleanedTail[5..].Trim();

        // If alias is known and condition does not already use it, prefix columns
        // NOTE: We trust the config; if it already has alias, skip prefixing.
        // Advanced alias-prefixing can be added here later if needed.

        var parser = new TSql160Parser(false);
        using var reader = new StringReader($"SELECT 1 WHERE ({cleanedTail})");
        var fragment = parser.Parse(reader, out var errors);

        if (errors.Count > 0 || fragment is not TSqlScript script || !script.Batches.Any())
            return; // Unparseable condition — skip injection rather than break SQL

        var statement = script.Batches[0].Statements[0] as SelectStatement;
        var tailExpr = (statement?.QueryExpression as QuerySpecification)?.WhereClause?.SearchCondition;

        if (tailExpr == null) return;

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
}
