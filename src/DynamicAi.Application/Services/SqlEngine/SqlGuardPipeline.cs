using DynamicAi.Application.Exceptions;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Security;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DynamicAi.Application.Services.SqlEngine;

/// <summary>
/// Single-pass SQL guard pipeline that parses rawSql ONCE with TSql160Parser and runs:
/// 1) Syntax + TOP/OFFSET + SecurityVisitor validation
/// 2) TableNameExtractor + SanitizationVisitor schema verification
/// 3) SqlHardeningVisitor (TOP injection, TailCondition verification, @CurrentCompanyId binding)
/// </summary>
public class SqlGuardPipeline : ISqlGuardPipeline
{
    private const int MaxRowsLimit = 1000;
    private readonly IDbSchemaProvider _schemaProvider;
    private readonly ILogger<SqlGuardPipeline> _logger;

    public SqlGuardPipeline(
        IDbSchemaProvider schemaProvider,
        ILogger<SqlGuardPipeline> logger)
    {
        _schemaProvider = schemaProvider;
        _logger = logger;
    }

    public async Task<RewrittenSqlResult> GuardAndHardenAsync(
        string rawSql,
        string tenantId,
        int? companyId,
        IEnumerable<TailConditionEntry>? tailConditions = null,
        CancellationToken ct = default,
        Task<Dictionary<string, HashSet<string>>>? schemaPrefetchTask = null,
        string? connectionString = null)
    {
        // 1. Single Parse
        var parser = new TSql160Parser(false);
        IList<ParseError> errors;
        TSqlFragment fragment;

        using (var reader = new StringReader(rawSql))
        {
            fragment = parser.Parse(reader, out errors);
        }

        if (errors.Any())
        {
            foreach (var error in errors)
            {
                _logger.LogError("  - SQL Syntax Error at Line {Line}, Column {Column}: {Message}", error.Line, error.Column, error.Message);
            }
            throw new InvalidDataException($"SQL Syntax Error: {errors.First().Message}");
        }

        // 2. Validation (TOP + OFFSET and SecurityVisitor)
        var topOffsetChecker = new TopOffsetChecker();
        fragment.Accept(topOffsetChecker);

        if (topOffsetChecker.HasTopAndOffset)
        {
            throw new InvalidDataException(
                "SQL Validation Failed: The query contains both TOP and OFFSET clauses. " +
                "T-SQL does not allow combining TOP with OFFSET (even in nested queries). " +
                "Please use OFFSET/FETCH ONLY for paging.");
        }

        SecurityVisitor.ValidateReadOnlyScript(fragment);

        // 3. Schema Sanitization
        await ValidateSchemaAsync(fragment, schemaPrefetchTask, connectionString, ct);

        // 4. Hardening
        var entries = tailConditions?.ToList() ?? new List<TailConditionEntry>();
        var parameters = new Dictionary<string, object>();

        if (companyId.HasValue && rawSql.Contains("@CurrentCompanyId", StringComparison.OrdinalIgnoreCase))
        {
            parameters["@CurrentCompanyId"] = companyId.Value;
        }

        var hardeningVisitor = new SqlHardeningVisitor(parameters, tenantId, MaxRowsLimit, entries);
        fragment.AcceptChildren(hardeningVisitor);

        var sqlGenerator = new Sql160ScriptGenerator();
        sqlGenerator.GenerateScript(fragment, out string rewrittenSql);

        _logger.LogInformation("[SqlGuardPipeline] Single-pass guard & hardening complete. Output length: {Len} chars", rewrittenSql.Length);
        return new RewrittenSqlResult(rewrittenSql, hardeningVisitor.Parameters);
    }

    private async Task ValidateSchemaAsync(
        TSqlFragment fragment,
        Task<Dictionary<string, HashSet<string>>>? schemaPrefetchTask,
        string? connectionString,
        CancellationToken ct)
    {
        Dictionary<string, HashSet<string>>? schemaMap;
        try
        {
            schemaMap = schemaPrefetchTask != null
                ? await schemaPrefetchTask
                : await _schemaProvider.GetSchemaMapAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SqlGuardPipeline] Error retrieving schema map. Skipping sanitization (Fail-Open).");
            return;
        }

        if (schemaMap == null || !schemaMap.Any())
        {
            _logger.LogWarning("[SqlGuardPipeline] Schema map is empty. Skipping sanitization (Fail-Open).");
            return;
        }

        var tableExtractor = new TableNameExtractor();
        fragment.Accept(tableExtractor);
        var allReferencedTables = tableExtractor.TableNames.ToList();

        if (!string.IsNullOrEmpty(connectionString))
        {
            var missingTables = allReferencedTables
                .Where(t => !schemaMap.ContainsKey(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (missingTables.Any())
            {
                try
                {
                    var extraSchema = await _schemaProvider.GetSchemaForViewsAsync(connectionString, missingTables, ct);
                    if (extraSchema.Count > 0)
                    {
                        schemaMap = new Dictionary<string, HashSet<string>>(schemaMap, StringComparer.OrdinalIgnoreCase);
                        foreach (var kvp in extraSchema)
                        {
                            schemaMap[kvp.Key] = kvp.Value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SqlGuardPipeline] On-demand schema fetch failed. Proceeding with partial schema.");
                }
            }
        }

        var visitor = new SanitizationVisitor(schemaMap);
        fragment.Accept(visitor);

        foreach (var warning in visitor.Warnings)
        {
            _logger.LogWarning(warning);
        }

        if (visitor.ValidationErrors.Any())
        {
            var errorSummary = string.Join("\n", visitor.ValidationErrors);
            _logger.LogWarning("[SqlGuardPipeline] Schema validation rejected SQL ({Count} violation(s)):\n{Errors}",
                visitor.ValidationErrors.Count, errorSummary);

            throw new SqlSanitizationException(
                $"SQL schema validation failed with {visitor.ValidationErrors.Count} error(s):\n{errorSummary}",
                visitor.ValidationErrors);
        }
    }
}
