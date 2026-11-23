using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;

namespace DynamicAi.Application.Interfaces;

/// <summary>
/// Guards, hardens, and executes generated SQL against the target database.
/// </summary>
public interface ISqlExecutionPipeline
{
    Task<QueryExecutionResult> ExecuteAsync(
        string rawSql,
        string tenantId,
        int? companyId,
        ShippedPageContext? structuredContext,
        int pageId,
        IntentHandlerContext? context,
        CancellationToken ct,
        Task<Dictionary<string, HashSet<string>>>? schemaPrefetchTask = null);
}
