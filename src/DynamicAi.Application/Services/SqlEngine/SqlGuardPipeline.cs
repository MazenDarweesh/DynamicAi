using System.Diagnostics;
using System.Security;
using DynamicAi.Application.Exceptions;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Models.DirectIntegration;
using DynamicAi.Application.Observability;
using DynamicAi.Application.Security;
using Microsoft.Extensions.Configuration;
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
    private readonly string _tableWhitelistMode;

    public SqlGuardPipeline(
        IDbSchemaProvider schemaProvider,
        ILogger<SqlGuardPipeline> logger,
        IConfiguration? configuration = null)
    {
        _schemaProvider = schemaProvider;
        _logger = logger;
        var mode = configuration?["Security:TableWhitelistMode"];
        _tableWhitelistMode = string.IsNullOrWhiteSpace(mode) ? "AuditOnly" : mode.Trim();
    }

    public async Task<RewrittenSqlResult> GuardAndHardenAsync(
        string rawSql,
        string tenantId,
        int? companyId,
        IEnumerable<TailConditionEntry>? tailConditions = null,
        CancellationToken ct = default,
        Task<Dictionary<string, HashSet<string>>>? schemaPrefetchTask = null,
        string? connectionString = null,
        IReadOnlySet<string>? allowedTables = null,
        bool skipCompanyFilter = false,
        string? masterViewName = null)
    {
        using var guardActivity = DynamicAiTelemetry.ActivitySource.StartActivity("sql.guard");
        bool securityOk = false;
        bool sanitizedOk = false;

        try
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
                    _logger.LogError("SQL Syntax Error at Line {Line}, Column {Column}: {Message}", error.Line, error.Column, error.Message);
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
            securityOk = true;
            guardActivity?.SetTag("sql.security_ok", true);

            // 3. Extract referenced tables & enforce Table Whitelist before schema fetch
            var tableExtractor = new TableNameExtractor();
            fragment.Accept(tableExtractor);
            var allReferencedTables = tableExtractor.TableNames.ToList();
            guardActivity?.SetTag("sql.tables_count", allReferencedTables.Count);

            EnforceTableWhitelist(allReferencedTables, allowedTables);

            var schemaMap = await ValidateSchemaAsync(fragment, allReferencedTables, schemaPrefetchTask, connectionString, ct);
            sanitizedOk = true;
            guardActivity?.SetTag("sql.sanitized_ok", true);

            // 4. Hardening
            var entries = tailConditions?.ToList() ?? new List<TailConditionEntry>();
            var rawMasterView = !string.IsNullOrWhiteSpace(masterViewName)
                ? masterViewName
                : entries.FirstOrDefault(e => e.SqlAlias.Equals("TM", StringComparison.OrdinalIgnoreCase))?.ViewName;
            var normalizedMasterView = SqlIdentifierNormalizer.NormalizeTableName(rawMasterView);
            var effectiveMasterView = !string.IsNullOrWhiteSpace(normalizedMasterView)
                ? normalizedMasterView
                : null;

            bool enforceCompanyFilter = ShouldEnforceCompanyFilter(
                skipCompanyFilter, companyId, effectiveMasterView, schemaMap);

            var parameters = new Dictionary<string, object>();

            if (companyId.HasValue && rawSql.Contains("@CurrentCompanyId", StringComparison.OrdinalIgnoreCase))
            {
                parameters["@CurrentCompanyId"] = companyId.Value;
            }

            var hardeningVisitor = new SqlHardeningVisitor(
                parameters,
                tenantId,
                MaxRowsLimit,
                entries,
                companyId,
                enforceCompanyFilter,
                effectiveMasterView);
            fragment.AcceptChildren(hardeningVisitor);

            var sqlGenerator = new Sql160ScriptGenerator();
            sqlGenerator.GenerateScript(fragment, out string rewrittenSql);

            _logger.LogInformation("[SqlGuardPipeline] Single-pass guard & hardening complete. Tables={TablesCount}, OutputChars={Len}",
                allReferencedTables.Count, rewrittenSql.Length);
            return new RewrittenSqlResult(rewrittenSql, hardeningVisitor.Parameters);
        }
        catch (SecurityException ex)
        {
            guardActivity?.SetTag("sql.security_ok", false);
            guardActivity?.SetTag("sql.sanitized_ok", sanitizedOk);
            guardActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            DynamicAiTelemetry.SecurityBlocked.Add(1,
                new KeyValuePair<string, object?>("reason", "security_exception"));
            throw;
        }
        catch (SqlSanitizationException ex)
        {
            guardActivity?.SetTag("sql.security_ok", securityOk);
            guardActivity?.SetTag("sql.sanitized_ok", false);
            guardActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            guardActivity?.SetTag("sql.security_ok", securityOk);
            guardActivity?.SetTag("sql.sanitized_ok", sanitizedOk);
            guardActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private void EnforceTableWhitelist(List<string> allReferencedTables, IReadOnlySet<string>? allowedTables)
    {
        if (allowedTables == null || allowedTables.Count == 0)
        {
            return;
        }

        var normalizedAllowed = allowedTables
            .Select(SqlIdentifierNormalizer.NormalizeTableName)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (normalizedAllowed.Count == 0)
        {
            return;
        }

        var disallowed = allReferencedTables
            .Select(SqlIdentifierNormalizer.NormalizeTableName)
            .Where(t => !string.IsNullOrWhiteSpace(t) && !normalizedAllowed.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (disallowed.Count == 0)
        {
            return;
        }

        var tablesList = string.Join(", ", disallowed);
        if (_tableWhitelistMode.Equals("Strict", StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException(
                $"Security Validation Failed: Access to table(s) '{tablesList}' is not allowed in the current page context.");
        }

        _logger.LogWarning(
            "[SqlGuardPipeline] AuditOnly: table(s) [{Tables}] referenced outside page whitelist",
            tablesList);
        DynamicAiTelemetry.SecurityBlocked.Add(1,
            new KeyValuePair<string, object?>("mode", "audit_only"));
    }

    private static bool ShouldEnforceCompanyFilter(
        bool skipCompanyFilter,
        int? companyId,
        string? masterViewName,
        Dictionary<string, HashSet<string>>? schemaMap)
    {
        if (skipCompanyFilter || !companyId.HasValue)
        {
            return false;
        }

        var normalizedMaster = SqlIdentifierNormalizer.NormalizeTableName(masterViewName);
        if (!string.IsNullOrWhiteSpace(normalizedMaster) && schemaMap != null)
        {
            HashSet<string>? masterColumns = null;
            if (!schemaMap.TryGetValue(normalizedMaster, out masterColumns))
            {
                foreach (var kvp in schemaMap)
                {
                    if (string.Equals(
                        SqlIdentifierNormalizer.NormalizeTableName(kvp.Key),
                        normalizedMaster,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        masterColumns = kvp.Value;
                        break;
                    }
                }
            }

            if (masterColumns != null &&
                !masterColumns.Contains("ComID", StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<Dictionary<string, HashSet<string>>?> ValidateSchemaAsync(
        TSqlFragment fragment,
        List<string> allReferencedTables,
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
            return null;
        }

        if (schemaMap == null || !schemaMap.Any())
        {
            _logger.LogWarning("[SqlGuardPipeline] Schema map is empty. Skipping sanitization (Fail-Open).");
            return schemaMap;
        }

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

        return schemaMap;
    }
}
