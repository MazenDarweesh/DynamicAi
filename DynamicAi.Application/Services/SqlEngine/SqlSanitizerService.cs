using DynamicAi.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DynamicAi.Application.Models.Sql;

namespace DynamicAi.Application.Services.SqlEngine
{
    public class SqlSanitizerService : ISqlSanitizerService
    {
        private readonly IDbSchemaProvider _schemaProvider;
        private readonly ILogger<SqlSanitizerService> _logger;

        public SqlSanitizerService(
            IDbSchemaProvider schemaProvider,
            ILogger<SqlSanitizerService> logger)
        {
            _schemaProvider = schemaProvider;
            _logger = logger;
        }

        public async Task<SanitizationResult> SanitizeAsync(string rawSql, string tenantId, CancellationToken ct = default)
        {
            var result = new SanitizationResult { SafeSql = rawSql, WasModified = false };

            try
            {
                // 1. Get Schema Map (Cached)
                var schemaMap = await _schemaProvider.GetSchemaMapAsync(ct);

                // 2. Parse SQL
                TSqlParser parser = new TSql160Parser(false);
                IList<ParseError> errors;
                TSqlFragment fragment;
                using (var reader = new StringReader(rawSql))
                {
                    fragment = parser.Parse(reader, out errors);
                }

                if (errors.Any())
                {
                    _logger.LogWarning("[SqlSanitizer] Parse errors detected. Skipping sanitization.");
                    return result; // Cannot sanitize broken SQL
                }

                // 3. Visit and Detect
                var visitor = new SanitizationVisitor(schemaMap);
                fragment.Accept(visitor);

                foreach (var warning in visitor.Warnings)
                {
                    _logger.LogWarning(warning);
                }

                if (visitor.ColumnsToRemove.Any())
                {
                    // 4. Rewrite SQL (The Surgery)
                    _logger.LogInformation("[SqlSanitizer] Found {Count} invalid columns. Rewriting SQL tree...", visitor.ColumnsToRemove.Count);
                    
                    var rewrittenSql = RemoveInvalidColumns(rawSql, fragment, visitor.ColumnsToRemove);
                    
                    result.SafeSql = rewrittenSql;
                    result.WasModified = true;
                    result.Changes = visitor.ColumnsToRemove
                        .Select(c => c.MultiPartIdentifier.Identifiers.Last().Value)
                        .Distinct()
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SqlSanitizer] Error during sanitization. Returning original SQL.");
                // Fail-safe: Return original SQL if sanitization crashes
            }

            return result;
        }

        /// <summary>
        /// يقوم بإعادة بناء الـ SQL مع حذف الأعمدة المحددة.
        /// </summary>
        private string RemoveInvalidColumns(string originalSql, TSqlFragment fragment, List<ColumnReferenceExpression> columnsToRemove)
        {
            // الطريقة الأفضل لحذف عناصر:
            // ScriptDom لا يدعم التعديل المباشر (Immutable-ish).
            // لكن يمكننا استخدام Tokens إذا أردنا الحفاظ على الـ Formatting، أو إعادة التوليد.
            // لإزالة عناصر من SELECT list، الأسهل هو تعديل الـ tokens قبل الطباعة،
            // أو الحل الأبسط: "Regenerate" باستخدام ScriptGenerator مع "Filter" مخصص؟ لا.
            
            // الحل العملي:
            // سنقوم بالبحث عن الـ SelectElements التي تحتوي على هذه الأعمدة ونزيلها من القائمة في الـ AST،
            // ثم نستخدم ScriptGenerator لتوليد SQL جديد.
            
            // 1. تجميع الـ SelectElements الآباء للأعمدة المحذوفة
            var parentFinder = new SelectElementFinder(columnsToRemove);
            fragment.Accept(parentFinder);
            
            var cleaner = new SelectElementRemover(parentFinder.ElementsToRemove);
            fragment.Accept(cleaner);

            // 1.5 إزالة الأعمدة من GROUP BY و ORDER BY
            var clauseCleaner = new ClauseElementRemover(columnsToRemove);
            fragment.Accept(clauseCleaner);

            // 1.6 إزالة الشروط من WHERE (إذا كان الشرط يحتوي على العمود المحذوف)
            // هذا معقد جداً في AST لأنه قد يكون جزء من شجرة AND/OR معقدة.
            // للتبسيط في هذا الـ MVP: إذا وجدنا العمود في WHERE سنحاول إزالة تعبير المقارنة الأقرب
            var whereCleaner = new WhereClauseRemover(columnsToRemove);
            fragment.Accept(whereCleaner);

            // 2. Generate New SQL
            var generator = new Sql160ScriptGenerator();
            string newSql;
            generator.GenerateScript(fragment, out newSql);
            
            return newSql;
        }

        // Helper Visitor to find the parent SelectElement for a given ColumnReference
        private class SelectElementFinder : TSqlFragmentVisitor
        {
            private readonly HashSet<ColumnReferenceExpression> _targets;
            public HashSet<SelectElement> ElementsToRemove { get; } = new HashSet<SelectElement>();

            public SelectElementFinder(List<ColumnReferenceExpression> targets)
            {
                _targets = new HashSet<ColumnReferenceExpression>(targets);
            }

            public override void Visit(SelectScalarExpression node)
            {
                // هل يحتوي هذا الـ Element على أي من الأعمدة المستهدفة؟
                // سنجرب البحث بداخله
                var colFinder = new ColumnInElementFinder(_targets);
                node.Accept(colFinder);

                if (colFinder.FoundAny)
                {
                    ElementsToRemove.Add(node);
                }
            }
        }

        // Helper to check if a specific element contains any bad columns
        private class ColumnInElementFinder : TSqlFragmentVisitor
        {
            private readonly HashSet<ColumnReferenceExpression> _targets;
            public bool FoundAny { get; private set; } = false;

            public ColumnInElementFinder(HashSet<ColumnReferenceExpression> targets)
            {
                _targets = targets;
            }

            public override void Visit(ColumnReferenceExpression node)
            {
                if (_targets.Contains(node))
                {
                    FoundAny = true;
                }
            }
        }

        // Helper to perform the actual removal from the AST list
        private class SelectElementRemover : TSqlFragmentVisitor
        {
            private readonly HashSet<SelectElement> _toRemove;

            public SelectElementRemover(HashSet<SelectElement> toRemove)
            {
                _toRemove = toRemove;
            }

            public override void Visit(QuerySpecification node)
            {
                // Remove matching elements from the list
                // Note: IList.Remove is safe while iterating backwards, or creating a new list.
                // ScriptDom lists are usually standard Lists.
                
                var validElements = new List<SelectElement>();
                foreach (var element in node.SelectElements)
                {
                    if (!_toRemove.Contains(element))
                    {
                        validElements.Add(element);
                    }
                }

                // إذا حذفنا كل الأعمدة! كارثة.
                if (validElements.Count == 0)
                {
                    // Fallback: Add '1 as ValidQuery' or '*' to prevent syntax error
                    // For now, let's strictly keep valid elements. If empty, the query is broken anyway.
                }
                else
                {
                    node.SelectElements.Clear();
                    foreach (var el in validElements) node.SelectElements.Add(el);
                }
            }
        }

        // Helper to remove elements from GROUP BY, ORDER BY
        private class ClauseElementRemover : TSqlFragmentVisitor
        {
            private readonly HashSet<ColumnReferenceExpression> _targets;

            public ClauseElementRemover(List<ColumnReferenceExpression> targets)
            {
                _targets = new HashSet<ColumnReferenceExpression>(targets);
            }

            public override void Visit(GroupByClause node)
            {
                var validElements = new List<GroupingSpecification>();
                foreach (var element in node.GroupingSpecifications)
                {
                    var finder = new ColumnInElementFinder(_targets);
                    element.Accept(finder);
                    if (!finder.FoundAny)
                    {
                        validElements.Add(element);
                    }
                }

                node.GroupingSpecifications.Clear();
                foreach (var el in validElements)
                {
                    node.GroupingSpecifications.Add(el);
                }
            }

            public override void Visit(OrderByClause node)
            {
                var validElements = new List<ExpressionWithSortOrder>();
                foreach (var element in node.OrderByElements)
                {
                    var finder = new ColumnInElementFinder(_targets);
                    element.Accept(finder);
                    if (!finder.FoundAny)
                    {
                        validElements.Add(element);
                    }
                }

                node.OrderByElements.Clear();
                foreach (var el in validElements) node.OrderByElements.Add(el);
            }
        }

        // ⚠️ التحذير: تبسيط مفرط لـ WHERE - في الواقع يجب إعادة كتابة الشجرة بعناية.
        // في هذا الإصدار، سنقوم بإزالة الشرط إذا كان BooleanComparisonExpression بسيط.
        private class WhereClauseRemover : TSqlFragmentVisitor
        {
            private readonly HashSet<ColumnReferenceExpression> _targets;

            public WhereClauseRemover(List<ColumnReferenceExpression> targets)
            {
                _targets = new HashSet<ColumnReferenceExpression>(targets);
            }

            public override void Visit(QuerySpecification node)
            {
                if (node.WhereClause == null) return;

                node.WhereClause.SearchCondition = PruneExpression(node.WhereClause.SearchCondition);
                
                // إذا تم مسح الشرط بالكامل
                if (node.WhereClause.SearchCondition == null)
                {
                    node.WhereClause = null;
                }
            }

            private BooleanExpression PruneExpression(BooleanExpression expr)
            {
                if (expr == null) return null;

                if (expr is BooleanBinaryExpression binExpr)
                {
                    var left = PruneExpression(binExpr.FirstExpression);
                    var right = PruneExpression(binExpr.SecondExpression);

                    if (left == null && right == null) return null;
                    if (left == null) return right;
                    if (right == null) return left;

                    binExpr.FirstExpression = left;
                    binExpr.SecondExpression = right;
                    return binExpr;
                }

                // BooleanComparisonExpression (A = B) or InPredicate or LikePredicate etc.
                var finder = new ColumnInElementFinder(_targets);
                expr.Accept(finder);
                if (finder.FoundAny)
                {
                    return null; // Remove this part of the condition
                }

                return expr;
            }
        }
    }
}
