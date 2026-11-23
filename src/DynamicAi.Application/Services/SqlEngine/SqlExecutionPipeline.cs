using System.Diagnostics;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Observability;
using DynamicAi.Application.Services.Handlers;
using Microsoft.Extensions.Logging;

namespace DynamicAi.Application.Services.SqlEngine;

/// <summary>
/// Coordinates single-pass SQL guarding/hardening via ISqlGuardPipeline and read-only execution via ISandboxedSqlExecutor.
/// </summary>
public class SqlExecutionPipeline : ISqlExecutionPipeline
{
    private readonly ISqlGuardPipeline _guardPipeline;
    private readonly ISandboxedSqlExecutor _executionService;
    private readonly ILogger<SqlExecutionPipeline> _logger;

    public SqlExecutionPipeline(
        ISqlGuardPipeline guardPipeline,
        ISandboxedSqlExecutor executionService,
        ILogger<SqlExecutionPipeline> logger)
    {
        _guardPipeline = guardPipeline;
        _executionService = executionService;
        _logger = logger;
    }

    public async Task<QueryExecutionResult> ExecuteAsync(
        string rawSql,
        string tenantId,
        int? companyId,
        ShippedPageContext? structuredContext,
        int pageId,
        IntentHandlerContext? context,
        CancellationToken ct,
        Task<Dictionary<string, HashSet<string>>>? schemaPrefetchTask = null)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
        {
            return new QueryExecutionResult
            {
                ErrorMessage = "Generated SQL was empty.",
                QueryLogId = context?.QueryLogId
            };
        }

        var tailEntries = TailConditionBuilder.BuildTailConditionEntries(structuredContext);

        _logger.LogInformation("[SqlExecutionPipeline] Guarding & hardening SQL ({Count} TailCondition entries)...", tailEntries.Count);

        var rewrittenResult = await _guardPipeline.GuardAndHardenAsync(
            rawSql,
            tenantId,
            companyId,
            tailEntries,
            ct,
            schemaPrefetchTask,
            context?.ConnectionString);

        var finalSql = rewrittenResult.RewrittenSql;

        _logger.LogInformation("[SqlExecutionPipeline] Executing hardened SQL (PageId={PageId})", pageId);
        IEnumerable<dynamic> executionResult;

        using (var execActivity = DynamicAiTelemetry.ActivitySource.StartActivity("sql.execute"))
        {
            execActivity?.SetTag("page.id", pageId);
            var sqlStart = Stopwatch.GetTimestamp();
            try
            {
                executionResult = await _executionService.ExecuteSqlAsync(
                    finalSql,
                    rewrittenResult.Parameters,
                    context?.ConnectionString,
                    ct);

                var sqlDurationMs = Stopwatch.GetElapsedTime(sqlStart).TotalMilliseconds;
                var rowsList = executionResult as ICollection<dynamic> ?? executionResult.ToList();
                executionResult = rowsList;
                var rowCount = rowsList.Count;

                execActivity?.SetTag("db.rows", rowCount);
                execActivity?.SetTag("db.duration_ms", sqlDurationMs);
                DynamicAiTelemetry.SqlDurationMs.Record(sqlDurationMs,
                    new KeyValuePair<string, object?>("success", true));
            }
            catch (Exception ex)
            {
                var sqlDurationMs = Stopwatch.GetElapsedTime(sqlStart).TotalMilliseconds;
                execActivity?.SetTag("db.duration_ms", sqlDurationMs);
                execActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                DynamicAiTelemetry.SqlDurationMs.Record(sqlDurationMs,
                    new KeyValuePair<string, object?>("success", false));
                DynamicAiTelemetry.SqlErrors.Add(1,
                    new KeyValuePair<string, object?>("error.type", ex.GetType().Name));
                throw;
            }
        }

        var queryInsights = new List<string>();
        if (structuredContext != null)
        {
            int columnsCount = structuredContext.MasterSetup.Columns.Count +
                               structuredContext.DetailSetups.Sum(d => d.Columns.Count);
            queryInsights.Add($"📚 {columnsCount} Column Definitions Loaded");
        }
        queryInsights.Add("🤖 Dynamic Generated Query");

        return new QueryExecutionResult
        {
            Data = executionResult,
            QueryLogId = context?.QueryLogId,
            IsCached = false,
            RewrittenSql = finalSql,
            QueryInsights = new QueryInsightData { RetrievedContext = queryInsights },
            PageId = pageId
        };
    }
}
