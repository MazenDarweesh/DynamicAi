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

        foreach (var detail in finalContext.DetailSetups ?? new())
        {
            if (!string.IsNullOrWhiteSpace(detail.TailCondition))
            {
                tailEntries.Add(new TailConditionEntry
                {
                    ViewName = detail.TableName,
                    SqlAlias = "TD",
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

            promptTail.AppendLine("RESERVED ALIASES (FIXED — must be used exactly as shown — cannot be reassigned or reused):");
            promptTail.AppendLine($"  TM → Master view: {finalContext.MasterSetup?.TableName} (always in FROM clause)");

            foreach (var detail in finalContext.DetailSetups ?? new())
            {
                promptTail.AppendLine($"  TD → Detail view: {detail.TableName} (JOIN only when the question requires detail-level columns)");
            }

            promptTail.AppendLine("  T1, T2, T3… → Available for dropdown lookup JOINs only (never for master/detail).");
            promptTail.AppendLine();
            promptTail.AppendLine("MANDATORY FILTERS:");
            foreach (var entry in tailEntries)
            {
                promptTail.AppendLine(
                    $"  • View='{entry.ViewName}' | Alias='{entry.SqlAlias}' → CONDITION: ({entry.Condition}). " +
                    $"Prefix every column in this condition with '{entry.SqlAlias}.' (e.g. {entry.SqlAlias}.ColumnName). " +
                    (entry.SqlAlias == "TD"
                        ? "⚠️ Apply this filter ONLY if you JOIN TD. If TD is not in your query, SKIP this filter entirely."
                        : "Apply this filter unconditionally in the main WHERE clause."));
            }

            promptTail.AppendLine();
            promptTail.AppendLine("DROPDOWN LOOKUP JOINS (if needed per COLUMN RESOLUTION rule E): use T1, T2, T3… starting from T1.");

            if (isDirectedContext)
            {
                promptTail.AppendLine("\nDIRECTED CONTEXT ACTIVE:");
                promptTail.AppendLine($"- Page: '{finalContext.PageName}'");
                promptTail.AppendLine($"- TM (Master): {finalContext.MasterSetup?.TableName}");

                foreach (var detail in finalContext.DetailSetups ?? new())
                {
                    promptTail.AppendLine($"- TD (Detail): {detail.TableName} — join only if user needs detail columns");
                }
            }

            logger?.LogInformation("[TailConditionBuilder] 🛡️ {Count} TailCondition entries built. Aliases: TM=Master, TD=Detail. Dropdown lookups use T1,T2,T3…",
                tailEntries.Count);

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
