using DynamicAi.Application.Models.DirectIntegration;
using Microsoft.Extensions.Logging;
using System.Text;

namespace DynamicAi.Application.Services.Handlers;

/// <summary>
/// Builds mandatory TailCondition entries and prompt blocks from ShippedPageContext.
/// </summary>
public static class TailConditionBuilder
{
    public static List<TailConditionEntry> BuildTailConditionEntries(ShippedPageContext? finalContext)
    {
        var tailEntries = new List<TailConditionEntry>();
        if (finalContext == null) return tailEntries;

        if (finalContext.MasterSetup != null && !string.IsNullOrWhiteSpace(finalContext.MasterSetup.TailCondition))
        {
            tailEntries.Add(new TailConditionEntry
            {
                ViewName = finalContext.MasterSetup.TableName,
                SqlAlias = "TM",
                Condition = CleanCondition(finalContext.MasterSetup.TailCondition)
            });
        }

        var detailSetups = finalContext.DetailSetups ?? new();
        for (int i = 0; i < detailSetups.Count; i++)
        {
            var detail = detailSetups[i];
            if (!string.IsNullOrWhiteSpace(detail.TailCondition))
            {
                tailEntries.Add(new TailConditionEntry
                {
                    ViewName = detail.TableName,
                    SqlAlias = PageContextFormatter.GetDetailAlias(i, detailSetups.Count),
                    Condition = CleanCondition(detail.TailCondition)
                });
            }
        }

        return tailEntries;
    }

    public static (List<TailConditionEntry> Entries, string PromptBlock) BuildTailConditionBlock(
        ShippedPageContext? finalContext,
        bool isDirectedContext,
        ILogger? logger = null)
    {
        var tailEntries = BuildTailConditionEntries(finalContext);
        if (finalContext == null) return (tailEntries, string.Empty);

        if (tailEntries.Any())
        {
            var promptTail = new StringBuilder();
            var detailSetups = finalContext.DetailSetups ?? new();

            promptTail.AppendLine("RESERVED ALIASES (FIXED — must be used exactly as shown — cannot be reassigned or reused):");
            promptTail.AppendLine($"  TM → Master view: {finalContext.MasterSetup?.TableName} (always in FROM clause)");

            for (int i = 0; i < detailSetups.Count; i++)
            {
                var detail = detailSetups[i];
                var alias = PageContextFormatter.GetDetailAlias(i, detailSetups.Count);
                promptTail.AppendLine($"  {alias} → Detail view: {detail.TableName} (JOIN only when the question requires detail-level columns)");
            }

            promptTail.AppendLine("  T1, T2, T3… → Available for dropdown lookup JOINs only (never for master/detail).");
            promptTail.AppendLine();
            promptTail.AppendLine("MANDATORY FILTERS:");
            foreach (var entry in tailEntries)
            {
                var isDetail = entry.SqlAlias.StartsWith("TD", StringComparison.OrdinalIgnoreCase);
                promptTail.AppendLine(
                    $"  • View='{entry.ViewName}' | Alias='{entry.SqlAlias}' → CONDITION: ({entry.Condition}). " +
                    $"Prefix every column in this condition with '{entry.SqlAlias}.' (e.g. {entry.SqlAlias}.ColumnName). " +
                    (isDetail
                        ? $"⚠️ Apply this filter ONLY if you JOIN {entry.SqlAlias}. If {entry.SqlAlias} is not in your query, SKIP this filter entirely."
                        : "Apply this filter unconditionally in the main WHERE clause."));
            }

            promptTail.AppendLine();
            promptTail.AppendLine("DROPDOWN LOOKUP JOINS (if needed per COLUMN RESOLUTION RULE 2 (SearchName == ColumnName)): use T1, T2, T3… starting from T1.");

            if (isDirectedContext)
            {
                promptTail.AppendLine("\nDIRECTED CONTEXT ACTIVE:");
                promptTail.AppendLine($"- Page: '{finalContext.PageName}'");
                promptTail.AppendLine($"- TM (Master): {finalContext.MasterSetup?.TableName}");

                for (int i = 0; i < detailSetups.Count; i++)
                {
                    var detail = detailSetups[i];
                    var alias = PageContextFormatter.GetDetailAlias(i, detailSetups.Count);
                    promptTail.AppendLine($"- {alias} (Detail): {detail.TableName} — join only if user needs detail columns");
                }
            }

            var detailAliasNote = detailSetups.Count > 1 ? "TD1,TD2…=Detail" : "TD=Detail";
            logger?.LogInformation("[TailConditionBuilder] 🛡️ {Count} TailCondition entries built. Aliases: TM=Master, {DetailAliases}. Dropdown lookups use T1,T2,T3…",
                tailEntries.Count, detailAliasNote);

            return (tailEntries, promptTail.ToString().Trim());
        }

        logger?.LogInformation("[TailConditionBuilder] ℹ️ No TailConditions. Dropdown lookup aliases start from T1.");
        return (tailEntries, string.Empty);
    }

    private static string CleanCondition(string condition)
    {
        var cleanCond = condition.Trim();
        if (cleanCond.StartsWith("where", StringComparison.OrdinalIgnoreCase))
            cleanCond = cleanCond.Substring(5).Trim();
        return cleanCond;
    }
}
