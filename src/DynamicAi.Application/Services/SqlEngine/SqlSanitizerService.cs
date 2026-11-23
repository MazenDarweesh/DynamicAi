using DynamicAi.Application.Exceptions;
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
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║             [Layer 4.5: SQL SANITIZER] - SCHEMA CHECK                  ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            var result = new SanitizationResult { SafeSql = rawSql, WasModified = false };

            try
            {
                // 1. Get Schema Map from Global Cache (warmed at startup — no DB call)
                var schemaMap = await _schemaProvider.GetSchemaMapAsync(ct);

                return await RunCoreValidationAsync(rawSql, schemaMap, result, ct);
            }
            catch (SqlSanitizationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SqlSanitizer] Error during sanitization. Returning original SQL.");
                return result;
            }
        }

        /// <summary>
        /// Core logic shared between both SanitizeAsync overloads.
        /// Parses SQL, visits AST, checks columns, and throws/rewrites as needed.
        /// </summary>
        private async Task<SanitizationResult> RunCoreValidationAsync(
            string rawSql,
            Dictionary<string, HashSet<string>> schemaMap,
            SanitizationResult result,
            CancellationToken ct,
            string? connectionString = null)
        {
            // Fail-Open: if schema is empty, skip validation (e.g. Warmer not yet run)
            if (schemaMap == null || !schemaMap.Any())
            {
                _logger.LogWarning("[SqlSanitizer] Schema map is empty. Skipping validation (Fail-Open).");
                return result;
            }

            // Parse SQL into AST
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
                return result;
            }

            // ══════════════════════════════════════════════════════════════════════
            // 🔍 STEP: Discover extra tables the LLM added (e.g. lookup JOINs)
            // ══════════════════════════════════════════════════════════════════════
            var tableExtractor = new TableNameExtractor();
            fragment.Accept(tableExtractor);
            var allReferencedTables = tableExtractor.TableNames.ToList();

            _logger.LogInformation("[SqlSanitizer] 🔎 SQL References {Count} table(s): [{Tables}]", 
                allReferencedTables.Count, string.Join(", ", allReferencedTables));

            if (!string.IsNullOrEmpty(connectionString))
            {
                var missingTables = allReferencedTables
                    .Where(t => !schemaMap.ContainsKey(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (missingTables.Any())
                {
                    _logger.LogInformation(
                        "[SqlSanitizer] 📥 Found {Count} table(s) missing from local cache: [{Tables}]. ConnectionString present, attempting on-demand fetch...",
                        missingTables.Count, string.Join(", ", missingTables));

                    try
                    {
                        var fetchStart = DateTime.UtcNow;
                        var extraSchema = await _schemaProvider.GetSchemaForViewsAsync(connectionString, missingTables, ct);
                        var duration = (DateTime.UtcNow - fetchStart).TotalMilliseconds;

                        foreach (var kvp in extraSchema)
                            schemaMap[kvp.Key] = kvp.Value;

                        _logger.LogInformation(
                            "[SqlSanitizer] ✅ Targeted Fetch Complete ({Duration:F0}ms). Merged {Fetched}/{Requested} missing tables into schema map.",
                            duration, extraSchema.Count, missingTables.Count);
                        
                        if (extraSchema.Count < missingTables.Count)
                        {
                            var failed = missingTables.Where(t => !extraSchema.ContainsKey(t)).ToList();
                            _logger.LogWarning("[SqlSanitizer] ⚠️ Could not resolve schema for: [{Failed}]. These might be hallucinated or deleted tables.", 
                                string.Join(", ", failed));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[SqlSanitizer] ❌ On-demand schema fetch failed. Proceeding with partial schema.");
                    }
                }
                else
                {
                    _logger.LogInformation("[SqlSanitizer] ✨ All referenced tables are already present in the prefetched schema map.");
                }
            }

            // Visit AST and detect invalid columns
            var visitor = new SanitizationVisitor(schemaMap);
            fragment.Accept(visitor);

            foreach (var warning in visitor.Warnings)
                _logger.LogWarning(warning);

            // STRICT MODE: throw on validation errors so the retry loop can send them back to LLM
            if (visitor.ValidationErrors.Any())
            {
                var errorSummary = string.Join("\n", visitor.ValidationErrors);
                _logger.LogWarning("[SqlSanitizer] 🛡️ STRICT MODE: {Count} violation(s):\n{Errors}",
                    visitor.ValidationErrors.Count, errorSummary);

                throw new SqlSanitizationException(
                    $"SQL schema validation failed with {visitor.ValidationErrors.Count} error(s):\n{errorSummary}",
                    visitor.ValidationErrors);
            }

            return result;
        }

        /// <summary>
        /// [Per-Request Path] يستخدم Schema Task بدأت مسبقاً بالتوازي مع الـ LLM.
        /// عند الاستدعاء: إما أن الـ Task خلصت (0ms await) أو لا تزال تعمل (await ينتظرها).
        /// </summary>
        public async Task<SanitizationResult> SanitizeAsync(
            string rawSql,
            string tenantId,
            Task<Dictionary<string, HashSet<string>>> schemaTask,
            CancellationToken ct = default,
            string? connectionString = null)
        {
            _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║         [Layer 4.5: SQL SANITIZER] - TARGETED SCHEMA CHECK             ║");
            _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

            var result = new SanitizationResult { SafeSql = rawSql, WasModified = false };

            try
            {
                // ⏳ Await the schema task that started in parallel with the LLM call.
                // If the LLM took 3s and schema fetch took 0.2s → this await costs 0ms.
                // If somehow schema is slower → we wait here, but that's exceptional.
                var schemaMap = await schemaTask;

                if (schemaMap == null || !schemaMap.Any())
                {
                    _logger.LogWarning("[SqlSanitizer] Targeted schema is empty. Skipping sanitization (Fail-Open).");
                    return result;
                }

                _logger.LogInformation("[SqlSanitizer] 🎯 Validating against targeted schema: {ViewCount} views, {ColCount} total columns.",
                    schemaMap.Count, schemaMap.Values.Sum(s => s.Count));

                // Reuse the same core validation logic — pass connectionString so extra JOINed tables can be auto-fetched
                return await RunCoreValidationAsync(rawSql, schemaMap, result, ct, connectionString);
            }
            catch (SqlSanitizationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SqlSanitizer] Error during targeted sanitization. Returning original SQL.");
                return result;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // TableNameExtractor — extracts all real table/view names from AST.
    // Used to discover extra tables the LLM joined (e.g. lookup tables).
    // Only collects NamedTableReference nodes (FROM / JOIN targets).
    // ══════════════════════════════════════════════════════════════════════
    internal class TableNameExtractor : TSqlFragmentVisitor
    {
        public HashSet<string> TableNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public override void Visit(NamedTableReference node)
        {
            var name = node.SchemaObject?.BaseIdentifier?.Value;
            if (!string.IsNullOrWhiteSpace(name))
                TableNames.Add(name);
        }
    }
}
