using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Domain.Entities;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.Text;

namespace DynamicAi.Application.Services.Handlers;

/// <summary>
/// Formats ShippedPageContext into the compact pipe-delimited prompt representation
/// and extracts table names / alias mappings from page context.
/// </summary>
public static class PageContextFormatter
{
    private static readonly TSqlParser SqlParser = new TSql160Parser(false);

    /// <summary>
    /// Compresses ShippedPageContext into compact pipe-delimited format for the LLM prompt.
    /// </summary>
    public static string CompressPageContextForPrompt(ShippedPageContext ctx, List<ColumnSynonym> approvedSynonyms)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"PageId={ctx.PageId} | PageName={ctx.PageName}");
        sb.AppendLine();

        sb.AppendLine($"[MASTER] Table={ctx.MasterSetup.TableName} | PK={ctx.MasterSetup.PrimaryKey} | Alias=TM");
        if (!string.IsNullOrWhiteSpace(ctx.MasterSetup.TailCondition))
            sb.AppendLine($"[MASTER.TailCondition] {ctx.MasterSetup.TailCondition.Trim()}");

        sb.AppendLine("[MASTER.COLUMNS] SearchName | ColumnName | InsertType | arLabel | enLabel | DroInfo");
        AppendColumnRows(sb, ctx.MasterSetup.TableName, ctx.MasterSetup.Columns, approvedSynonyms);
        sb.AppendLine();

        foreach (var detail in ctx.DetailSetups ?? new())
        {
            sb.AppendLine($"[DETAIL] Table={detail.TableName} | FK={detail.ForeignKey} | Alias=TD");
            if (!string.IsNullOrWhiteSpace(detail.TailCondition))
                sb.AppendLine($"[DETAIL.TailCondition] {detail.TailCondition.Trim()}");

            sb.AppendLine("[DETAIL.COLUMNS] SearchName | ColumnName | InsertType | arLabel | enLabel | DroInfo");
            AppendColumnRows(sb, detail.TableName, detail.Columns, approvedSynonyms);
            sb.AppendLine();
        }

        if (ctx.BusinessRules?.Any() == true)
        {
            sb.AppendLine("[BUSINESS_RULES]");
            foreach (var rule in ctx.BusinessRules)
                sb.AppendLine($"  • {rule}");
        }

        return sb.ToString().Trim();
    }

    private static void AppendColumnRows(
        StringBuilder sb,
        string tableName,
        IEnumerable<DynamicAi.Application.Models.DirectIntegration.ColumnDefinition> columns,
        List<ColumnSynonym> approvedSynonyms)
    {
        foreach (var col in columns)
        {
            var dro = col.DroModel != null
                ? $"Dro:{col.DroModel}:{col.DroValue}:{col.DroText}"
                : string.Empty;

            var colSynonyms = approvedSynonyms
                .Where(s => s.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase)
                         && s.ColumnName.Equals(col.ColumnName, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.UserTerm)
                .ToList();

            var synonymPart = colSynonyms.Any() ? $" (مرادفات: {string.Join("، ", colSynonyms)})" : string.Empty;
            sb.AppendLine($"  {col.SearchName} | {col.ColumnName} | {col.InsertType} | {col.arColumnLabel}{synonymPart} | {col.enColumnLabel} | {dro}");
        }
    }

    public static List<string> ExtractTablesFromTailConditions(ShippedPageContext context)
    {
        var allConditions = new List<string>();

        if (!string.IsNullOrEmpty(context.MasterSetup?.TailCondition))
            allConditions.Add(context.MasterSetup.TailCondition);
        foreach (var d in context.DetailSetups ?? new())
            if (!string.IsNullOrEmpty(d.TailCondition))
                allConditions.Add(d.TailCondition);

        var tableVisitor = new ExtractTablesVisitor();

        foreach (var cond in allConditions)
        {
            var cleanCond = cond.Trim();
            if (cleanCond.StartsWith("where", StringComparison.OrdinalIgnoreCase))
                cleanCond = cleanCond.Substring(5).Trim();

            var dummyQuery = $"SELECT 1 FROM DUMMY WHERE {cleanCond}";
            using var reader = new StringReader(dummyQuery);
            var fragment = SqlParser.Parse(reader, out IList<ParseError> errors);
            if (!errors.Any())
            {
                fragment.Accept(tableVisitor);
            }
        }

        return tableVisitor.Tables.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string? ResolveAliasToTable(string mappedToColumn, ShippedPageContext? ctx)
    {
        if (ctx == null || !mappedToColumn.Contains('.')) return null;
        var alias = mappedToColumn.Split('.')[0].Trim().ToUpperInvariant();
        return alias switch
        {
            "TM" => ctx.MasterSetup?.TableName,
            "TD" => ctx.DetailSetups?.FirstOrDefault()?.TableName,
            _ => null
        };
    }

    private sealed class ExtractTablesVisitor : TSqlFragmentVisitor
    {
        public List<string> Tables { get; } = new();

        public override void Visit(NamedTableReference node)
        {
            if (node.SchemaObject?.BaseIdentifier?.Value != null)
            {
                var tableName = node.SchemaObject.BaseIdentifier.Value;
                if (!tableName.Equals("DUMMY", StringComparison.OrdinalIgnoreCase))
                {
                    Tables.Add(tableName);
                }
            }
            base.Visit(node);
        }
    }
}
