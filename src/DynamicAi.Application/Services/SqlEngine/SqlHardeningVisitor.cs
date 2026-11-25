using System.Text.RegularExpressions;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Services.SqlEngine;
using Microsoft.SqlServer.TransactSql.ScriptDom;

/// <summary>
/// (الزائر "الحارس" الذي يقوم بالتحصين - Layer 4.8)
///
/// مسؤوليات هذا الزائر:
///   1. حقن TOP للحد من حجم النتائج.
///   2. التحقق من وجود فلتر الشركة ComID = @CurrentCompanyId على الاستعلام الخارجي وحقنه عند الحاجة.
///   3. التحقق عبر AST من وجود كل TailCondition المرتبطة بكل مصدر بياني (View/Table) وحقن الناقص منها
///      في الـ QuerySpecification الصحيح وفق تطابق (ViewName, SqlAlias).
/// </summary>
public class SqlHardeningVisitor : TSqlFragmentVisitor
{
    public Dictionary<string, object> Parameters { get; private set; }
    private readonly string _tenantId;
    private readonly int _topRows;
    private readonly List<TailConditionEntry> _tailConditions;
    private readonly int? _companyId;
    private readonly bool _enforceCompanyFilter;
    private readonly string? _masterViewName;

    // Maps each QuerySpecification to the ordered list of (TableName, Alias) in its FROM/JOIN clauses.
    private readonly Dictionary<QuerySpecification, List<(string TableName, string? Alias)>> _queryTables = new();
    private readonly Stack<QuerySpecification> _queryStack = new();

    private QuerySpecification? _outermostQuerySpec;
    private bool _topInjected = false;
    private bool _companyFilterApplied = false;

    public SqlHardeningVisitor(
        Dictionary<string, object> parameters,
        string tenantId,
        int topRows,
        IEnumerable<TailConditionEntry>? tailConditions = null,
        int? companyId = null,
        bool enforceCompanyFilter = false,
        string? masterViewName = null)
    {
        Parameters = parameters;
        _tenantId = tenantId;
        _topRows = topRows;
        _companyId = companyId;
        _enforceCompanyFilter = enforceCompanyFilter;
        var normalizedMaster = SqlIdentifierNormalizer.NormalizeTableName(masterViewName);
        _masterViewName = string.IsNullOrWhiteSpace(normalizedMaster) ? null : normalizedMaster;
        _tailConditions = tailConditions?
            .Where(e => !string.IsNullOrWhiteSpace(e.Condition) &&
                        !string.IsNullOrWhiteSpace(SqlIdentifierNormalizer.NormalizeTableName(e.ViewName)))
            .ToList() ?? new List<TailConditionEntry>();
    }

    public override void Visit(SelectStatement node)
    {
        _outermostQuerySpec ??= node.QueryExpression as QuerySpecification;
        base.Visit(node);
    }

    public override void ExplicitVisit(QuerySpecification node)
    {
        _queryStack.Push(node);

        if (!_queryTables.ContainsKey(node))
            _queryTables[node] = new List<(string TableName, string? Alias)>();

        if (!_topInjected && node.TopRowFilter == null && node.OffsetClause == null)
        {
            node.TopRowFilter = new TopRowFilter
            {
                Expression = new IntegerLiteral { Value = _topRows.ToString() }
            };
            _topInjected = true;
        }

        node.AcceptChildren(this);

        bool isOutermost = _outermostQuerySpec != null
            ? ReferenceEquals(node, _outermostQuerySpec)
            : _queryStack.Count == 1;

        if (isOutermost && !_companyFilterApplied && _enforceCompanyFilter && _companyId.HasValue)
        {
            EnsureCompanyFilter(node);
            _companyFilterApplied = true;
        }

        if (_tailConditions.Any())
        {
            ApplyMissingConditionsToQuery(node);
        }

        _queryStack.TryPop(out _);
    }

    /// <summary>
    /// Registers each FROM/JOIN NamedTableReference with its exact TableName and Alias
    /// under the nearest enclosing QuerySpecification.
    /// </summary>
    public override void ExplicitVisit(NamedTableReference node)
    {
        if (_queryStack.TryPeek(out var currentQuery))
        {
            var tableName = SqlIdentifierNormalizer.NormalizeTableName(node.SchemaObject?.BaseIdentifier?.Value);
            var alias = node.Alias?.Value;
            if (!string.IsNullOrEmpty(tableName) || !string.IsNullOrEmpty(alias))
            {
                if (!_queryTables.TryGetValue(currentQuery, out var list))
                {
                    list = new List<(string TableName, string? Alias)>();
                    _queryTables[currentQuery] = list;
                }
                list.Add((tableName, alias));
            }
        }
    }

    private void EnsureCompanyFilter(QuerySpecification queryNode)
    {
        if (!_companyId.HasValue) return;

        Parameters["@CurrentCompanyId"] = _companyId.Value;

        if (queryNode.WhereClause != null)
        {
            var checkVisitor = new CompanyFilterCheckVisitor();
            queryNode.WhereClause.Accept(checkVisitor);
            if (checkVisitor.Found)
            {
                return;
            }
        }

        string? masterAlias = ResolveMasterAlias(queryNode);
        var identifier = new MultiPartIdentifier();
        if (!string.IsNullOrWhiteSpace(masterAlias))
        {
            identifier.Identifiers.Add(new Identifier { Value = masterAlias });
        }
        identifier.Identifiers.Add(new Identifier { Value = "ComID" });

        var companyComparison = new BooleanComparisonExpression
        {
            ComparisonType = BooleanComparisonType.Equals,
            FirstExpression = new ColumnReferenceExpression { MultiPartIdentifier = identifier },
            SecondExpression = new VariableReference { Name = "@CurrentCompanyId" }
        };

        AppendAndCondition(queryNode, companyComparison, wrapNewConditionInParens: false);
    }

    private string? ResolveMasterAlias(QuerySpecification queryNode)
    {
        if (!_queryTables.TryGetValue(queryNode, out var tables) || tables.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(_masterViewName))
        {
            var normalizedMaster = SqlIdentifierNormalizer.NormalizeTableName(_masterViewName);
            var match = tables.FirstOrDefault(t =>
                SqlIdentifierNormalizer.NormalizeTableName(t.TableName)
                    .Equals(normalizedMaster, StringComparison.OrdinalIgnoreCase));
            return !string.IsNullOrEmpty(match.TableName) ? match.Alias : null;
        }

        return tables[0].Alias;
    }

    private void ApplyMissingConditionsToQuery(QuerySpecification queryNode)
    {
        if (!_queryTables.TryGetValue(queryNode, out var tables) || tables.Count == 0)
            return;

        foreach (var entry in _tailConditions)
        {
            if (!TryResolveAliasInQuery(tables, entry, out var effectiveAlias))
                continue;

            if (IsConditionAlreadyInWhere(queryNode, entry.Condition, effectiveAlias))
                continue;

            InjectCondition(queryNode, entry.Condition, effectiveAlias);
        }
    }

    private bool TryResolveAliasInQuery(
        List<(string TableName, string? Alias)> tables,
        TailConditionEntry entry,
        out string? effectiveAlias)
    {
        var normalizedEntryView = SqlIdentifierNormalizer.NormalizeTableName(entry.ViewName);

        // 1. Exact (ViewName, SqlAlias) match — prevents TD1/TD2 cross-contamination.
        if (!string.IsNullOrWhiteSpace(entry.SqlAlias))
        {
            var exact = tables.FirstOrDefault(t =>
                SqlIdentifierNormalizer.NormalizeTableName(t.TableName)
                    .Equals(normalizedEntryView, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Alias, entry.SqlAlias, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(exact.TableName))
            {
                effectiveAlias = exact.Alias;
                return true;
            }
        }

        // 2. Fallback: ViewName matches with a different/absent alias only when unambiguous
        //    (single entry for that ViewName, and the table's alias isn't owned by another entry).
        int entriesForView = _tailConditions.Count(e =>
            SqlIdentifierNormalizer.NormalizeTableName(e.ViewName)
                .Equals(normalizedEntryView, StringComparison.OrdinalIgnoreCase));
        if (entriesForView == 1)
        {
            var claimedAliases = _tailConditions
                .Where(e => !ReferenceEquals(e, entry) && !string.IsNullOrWhiteSpace(e.SqlAlias))
                .Select(e => e.SqlAlias)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var fallback = tables.FirstOrDefault(t =>
                SqlIdentifierNormalizer.NormalizeTableName(t.TableName)
                    .Equals(normalizedEntryView, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(t.Alias) || !claimedAliases.Contains(t.Alias)));

            if (!string.IsNullOrEmpty(fallback.TableName))
            {
                effectiveAlias = string.IsNullOrWhiteSpace(fallback.Alias) ? null : fallback.Alias;
                return true;
            }
        }

        effectiveAlias = null;
        return false;
    }

    private static bool IsConditionAlreadyInWhere(
        QuerySpecification node,
        string condition,
        string? effectiveAlias)
    {
        if (node.WhereClause?.SearchCondition == null)
            return false;

        var unaliasedTailExpr = ParseConditionExpression(condition);
        if (unaliasedTailExpr == null)
            return false;

        var whereConjuncts = ExtractConjuncts(node.WhereClause.SearchCondition)
            .Select(NormalizeExpression)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unaliasedConjuncts = ExtractConjuncts(unaliasedTailExpr)
            .Select(NormalizeExpression)
            .ToList();

        if (unaliasedConjuncts.Count == 0)
            return false;

        List<string>? aliasedConjuncts = null;
        if (!string.IsNullOrWhiteSpace(effectiveAlias))
        {
            var aliasedTailExpr = ParseConditionExpression(condition);
            if (aliasedTailExpr != null)
            {
                aliasedTailExpr.Accept(new AliasInjectorVisitor(effectiveAlias));
                aliasedConjuncts = ExtractConjuncts(aliasedTailExpr)
                    .Select(NormalizeExpression)
                    .ToList();
            }
        }

        for (int i = 0; i < unaliasedConjuncts.Count; i++)
        {
            bool matchedUnaliased = whereConjuncts.Contains(unaliasedConjuncts[i]);
            bool matchedAliased = aliasedConjuncts != null &&
                                  i < aliasedConjuncts.Count &&
                                  whereConjuncts.Contains(aliasedConjuncts[i]);

            if (!matchedUnaliased && !matchedAliased)
            {
                return false;
            }
        }

        return true;
    }

    private static void InjectCondition(
        QuerySpecification queryNode,
        string condition,
        string? effectiveAlias)
    {
        var tailExpr = ParseConditionExpression(condition);
        if (tailExpr == null) return;

        if (!string.IsNullOrWhiteSpace(effectiveAlias))
        {
            tailExpr.Accept(new AliasInjectorVisitor(effectiveAlias));
        }

        AppendAndCondition(queryNode, tailExpr, wrapNewConditionInParens: true);
    }

    private static void AppendAndCondition(
        QuerySpecification queryNode,
        BooleanExpression newCondition,
        bool wrapNewConditionInParens)
    {
        if (queryNode.WhereClause == null)
        {
            queryNode.WhereClause = new WhereClause { SearchCondition = newCondition };
            return;
        }

        queryNode.WhereClause.SearchCondition = new BooleanBinaryExpression
        {
            FirstExpression = new BooleanParenthesisExpression
            {
                Expression = queryNode.WhereClause.SearchCondition
            },
            SecondExpression = wrapNewConditionInParens
                ? new BooleanParenthesisExpression { Expression = newCondition }
                : newCondition,
            BinaryExpressionType = BooleanBinaryExpressionType.And
        };
    }

    private static BooleanExpression? ParseConditionExpression(string condition)
    {
        string cleanedTail = condition.Trim();
        if (cleanedTail.StartsWith("WHERE", StringComparison.OrdinalIgnoreCase))
        {
            cleanedTail = cleanedTail[5..].Trim();
        }

        var parser = new TSql160Parser(false);
        using var reader = new StringReader($"SELECT 1 WHERE ({cleanedTail})");
        var fragment = parser.Parse(reader, out var errors);

        if (errors.Count > 0 || fragment is not TSqlScript script || !script.Batches.Any())
            return null;

        var statement = script.Batches[0].Statements[0] as SelectStatement;
        var expr = (statement?.QueryExpression as QuerySpecification)?.WhereClause?.SearchCondition;
        return expr == null ? null : UnwrapOuterParentheses(expr);
    }

    private static BooleanExpression UnwrapOuterParentheses(BooleanExpression expr)
    {
        while (expr is BooleanParenthesisExpression paren)
            expr = paren.Expression;
        return expr;
    }

    private static IEnumerable<BooleanExpression> ExtractConjuncts(BooleanExpression expr)
    {
        expr = UnwrapOuterParentheses(expr);

        if (expr is BooleanBinaryExpression bin && bin.BinaryExpressionType == BooleanBinaryExpressionType.And)
        {
            foreach (var left in ExtractConjuncts(bin.FirstExpression))
                yield return left;
            foreach (var right in ExtractConjuncts(bin.SecondExpression))
                yield return right;
        }
        else
        {
            yield return expr;
        }
    }

    private static string NormalizeExpression(BooleanExpression expr)
    {
        expr = UnwrapOuterParentheses(expr);
        var gen = new Sql160ScriptGenerator();
        gen.GenerateScript(expr, out var script);
        return Regex.Replace(script, @"\s+", " ").Trim().ToLowerInvariant();
    }

    public override void Visit(StringLiteral node) { /* Parameter binding deferred to Dapper */ }
    public override void Visit(FromClause node) { /* Tenant isolation handled by the views */ }

    private sealed class CompanyFilterCheckVisitor : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void ExplicitVisit(QuerySpecification node)
        {
            // Do not descend into subqueries in WHERE when checking the current scope's filter.
        }

        public override void Visit(BooleanComparisonExpression node)
        {
            if (Found) return;

            if (node.ComparisonType == BooleanComparisonType.Equals &&
                ((IsComIdColumn(node.FirstExpression) && IsCurrentCompanyVar(node.SecondExpression)) ||
                 (IsComIdColumn(node.SecondExpression) && IsCurrentCompanyVar(node.FirstExpression))))
            {
                Found = true;
            }

            base.Visit(node);
        }

        private static bool IsComIdColumn(ScalarExpression? expr) =>
            expr is ColumnReferenceExpression col &&
            col.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value?
                .Equals("ComID", StringComparison.OrdinalIgnoreCase) == true;

        private static bool IsCurrentCompanyVar(ScalarExpression? expr) =>
            expr is VariableReference varRef &&
            varRef.Name.Equals("@CurrentCompanyId", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Injects the table alias onto unaliased columns in the outer predicate scope only,
    /// skipping nested subqueries.
    /// </summary>
    private sealed class AliasInjectorVisitor : TSqlFragmentVisitor
    {
        private readonly string _alias;

        public AliasInjectorVisitor(string alias)
        {
            _alias = alias;
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            // Intentionally empty — do not descend into subqueries.
        }

        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier != null && node.MultiPartIdentifier.Identifiers.Count == 1)
            {
                node.MultiPartIdentifier.Identifiers.Insert(0, new Identifier { Value = _alias });
            }
        }
    }
}
