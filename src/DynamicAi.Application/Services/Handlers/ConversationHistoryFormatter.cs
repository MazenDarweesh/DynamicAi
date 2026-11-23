using DynamicAi.Domain.Entities;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicAi.Application.Services.Handlers;

/// <summary>
/// Formats page-isolated conversation history into a compact prompt block.
/// </summary>
public static class ConversationHistoryFormatter
{
    public static string BuildConversationHistoryBlock(
        List<ConversationTurn>? conversationHistory,
        int currentPageId,
        ILogger? logger = null)
    {
        if (conversationHistory == null || !conversationHistory.Any()) return string.Empty;

        var allSqlTurns = conversationHistory.Where(t => !string.IsNullOrEmpty(t.SqlExecuted)).ToList();

        var turnsWithSql = allSqlTurns
            .Where(t => (t.PageId ?? 0) == currentPageId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(5)
            .ToList();

        if (allSqlTurns.Count > turnsWithSql.Count)
        {
            logger?.LogWarning("🔪 [ConversationHistory] HISTORY SLICED: Removed {HiddenTurns} unrelated prior SQL queries from LLM context to prevent cross-page contamination (Current Context PageID: {CurrentPageId}).",
                allSqlTurns.Count - turnsWithSql.Count, currentPageId);
        }

        if (!turnsWithSql.Any()) return string.Empty;

        var historyBuilder = new StringBuilder();
        for (int i = 0; i < turnsWithSql.Count; i++)
        {
            historyBuilder.AppendLine($"[Turn {i + 1}] Q: \"{turnsWithSql[i].Question}\"");
            historyBuilder.AppendLine($"         SQL: {CompressSqlForHistory(turnsWithSql[i].SqlExecuted ?? string.Empty)}");
        }

        return historyBuilder.ToString().Trim();
    }

    public static string CompressSqlForHistory(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql;

        var result = Regex.Replace(sql, @"\bSELECT\s+TOP\s+\d+\s+", "SELECT ", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\s+", " ").Trim();
        result = Regex.Replace(result,
            @"\s*AND\s*\(\s*\(\s*1\s*=\s*1.*?\)\s*\)\s*;?\s*$",
            ";", RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();

        if (!result.EndsWith(";")) result += ";";
        return result;
    }
}
