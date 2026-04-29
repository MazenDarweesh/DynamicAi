using Microsoft.SqlServer.TransactSql.ScriptDom;

/// <summary>
/// (الزائر "الحارس" الذي يقوم بالتحصين - Layer 4.8)
/// يحقن TOP و TailCondition في الاستعلام الرئيسي فقط.
/// </summary>
internal class SqlHardeningVisitor : TSqlFragmentVisitor
{
    public Dictionary<string, object> Parameters { get; private set; }
    private readonly string _tenantId;
    private readonly int _topRows;
    private int _paramCounter = 0;
    private readonly string? _tailCondition;

    /// <summary>
    /// 🛡️ Guard Flag: يضمن حقن TailCondition مرة واحدة فقط على أعلى مستوى.
    /// يمنع الـ Visitor من الدخول في Infinite Recursion عند زيارة الـ Subqueries
    /// المحقونة من TailCondition نفسها (مثل: SELECT DepartmentId FROM ...).
    /// </summary>
    private bool _tailConditionInjected = false;

    public SqlHardeningVisitor(Dictionary<string, object> parameters, string tenantId, int topRows, string? tailCondition = null)
    {
        Parameters = parameters;
        _tenantId = tenantId;
        _topRows = topRows;
        _tailCondition = tailCondition;
    }

    // --- 1. تحصين: ربط الباراميترات (لمنع SQL Injection) ---
    public override void Visit(StringLiteral node)
    {
        // MVP: نؤجل ParameterBinding المعقد ونعتمد على Layer 3 و Dapper
    }

    // --- 2. تحصين: حقن TOP + TailCondition ---
    // ⚠️ CRITICAL: Do NOT add TOP if OFFSET is present - they are mutually exclusive in T-SQL!
    public override void Visit(QuerySpecification node)
    {
        // 1. TOP Injection: أضف TOP فقط إذا لم يكن موجوداً ولا OFFSET
        if (node.TopRowFilter == null && node.OffsetClause == null)
        {
            node.TopRowFilter = new TopRowFilter
            {
                Expression = new IntegerLiteral { Value = _topRows.ToString() }
            };
        }

        // 2. TailCondition Injection: يتم مرة واحدة فقط على الـ Outermost Query
        // الـ Flag يمنع الـ Visitor من تطبيق الشرط على الـ Subqueries داخل TailCondition نفسه
        if (!_tailConditionInjected && !string.IsNullOrWhiteSpace(_tailCondition))
        {
            _tailConditionInjected = true; // 🔒 أغلق الباب فوراً قبل base.Visit

            string cleanedTail = _tailCondition.Trim();
            if (cleanedTail.StartsWith("WHERE", StringComparison.OrdinalIgnoreCase))
            {
                cleanedTail = cleanedTail.Substring(5).Trim();
            }

            var parser = new TSql160Parser(false);
            using var reader = new StringReader($"SELECT 1 WHERE ({cleanedTail})");
            var fragment = parser.Parse(reader, out var errors);

            if (errors.Count == 0 && fragment is TSqlScript script && script.Batches.Count > 0)
            {
                var statement = script.Batches[0].Statements[0] as SelectStatement;
                var querySpec = statement?.QueryExpression as QuerySpecification;
                var tailExpr = querySpec?.WhereClause?.SearchCondition;

                if (tailExpr != null)
                {
                    if (node.WhereClause == null)
                    {
                        node.WhereClause = new WhereClause { SearchCondition = tailExpr };
                    }
                    else
                    {
                        var combined = new BooleanBinaryExpression
                        {
                            FirstExpression = new BooleanParenthesisExpression { Expression = node.WhereClause.SearchCondition },
                            SecondExpression = new BooleanParenthesisExpression { Expression = tailExpr },
                            BinaryExpressionType = BooleanBinaryExpressionType.And
                        };
                        node.WhereClause.SearchCondition = combined;
                    }
                }
            }
        }

        base.Visit(node);
    }

    // --- 3. تحصين: TenantId ---
    public override void Visit(FromClause node)
    {
        // MVP: Views تعالج Tenant Isolation داخلياً
    }
}
