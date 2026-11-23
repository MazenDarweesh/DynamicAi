using System.Diagnostics;
using System.Text.RegularExpressions;
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
        Task<Dictionary<string, HashSet<string>>>? schemaPrefetchTask = null,
        Dictionary<string, object?>? extraParameters = null)
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
        var allowedTables = BuildAllowedTables(structuredContext);
        var skipCompanyFilter = ShouldSkipCompanyFilter(structuredContext);
        var masterViewName = !string.IsNullOrWhiteSpace(structuredContext?.MasterSetup?.TableName)
            ? structuredContext.MasterSetup.TableName
            : null;

        _logger.LogInformation("[SqlExecutionPipeline] Guarding & hardening SQL ({Count} TailCondition entries)...", tailEntries.Count);

        var rewrittenResult = await _guardPipeline.GuardAndHardenAsync(
            rawSql,
            tenantId,
            companyId,
            tailEntries,
            ct,
            schemaPrefetchTask,
            context?.ConnectionString,
            allowedTables,
            skipCompanyFilter,
            masterViewName);

        var finalSql = rewrittenResult.RewrittenSql;

        var executionParameters = new Dictionary<string, object>(
            rewrittenResult.Parameters,
            StringComparer.OrdinalIgnoreCase);

        if (extraParameters != null)
        {
            foreach (var kvp in extraParameters)
            {
                executionParameters.TryAdd(kvp.Key, kvp.Value ?? DBNull.Value);
            }
        }

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
                    executionParameters,
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

    private static HashSet<string>? BuildAllowedTables(ShippedPageContext? structuredContext)
    {
        if (structuredContext == null) return null;

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(structuredContext.MasterSetup?.TableName))
            allowed.Add(structuredContext.MasterSetup.TableName);

        AddDropdownModels(allowed, structuredContext.MasterSetup?.Columns);

        foreach (var detail in structuredContext.DetailSetups ?? [])
        {
            if (!string.IsNullOrWhiteSpace(detail.TableName))
                allowed.Add(detail.TableName);

            AddDropdownModels(allowed, detail.Columns);
        }

        foreach (var tailTable in PageContextFormatter.ExtractTablesFromTailConditions(structuredContext))
        {
            if (!string.IsNullOrWhiteSpace(tailTable))
                allowed.Add(tailTable);
        }

        return allowed;
    }

    private static void AddDropdownModels(HashSet<string> allowed, IEnumerable<ColumnDefinition>? columns)
    {
        if (columns == null) return;
        foreach (var col in columns)
        {
            if (col.InsertType.Equals("dropdown", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(col.DroModel))
            {
                allowed.Add(col.DroModel);
            }
        }
    }

    private static bool ShouldSkipCompanyFilter(ShippedPageContext? structuredContext) =>
        structuredContext?.BusinessRules?.Any(rule =>
            !string.IsNullOrWhiteSpace(rule) &&
            Regex.IsMatch(rule, @"\bIsCompany\s*=\s*FALSE\b", RegexOptions.IgnoreCase)) == true;
}
