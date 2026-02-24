using DynamicAi.Application.DbConstants;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Models;
using DynamicAi.Application.Services.SqlEngine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace DynamicAi.Application.Services.Diagnostics;

/// <summary>
/// Diagnostic service that pulls ALL schema/metadata from Qdrant,
/// assembles it as a single text blob (like what Global Cache would contain),
/// and counts tokens via Gemini's CountTokens API.
/// 
/// Purpose: Determine if the entire schema fits in a single Gemini cache resource (1M token limit).
/// </summary>
public class SchemaTokenAnalyzerService : ISchemaTokenAnalyzerService
{
    private readonly IAppVectorDbService _vectorDb;
    private readonly ITokenCounter _tokenCounter;
    private readonly ILogger<SchemaTokenAnalyzerService> _logger;
    private readonly string _collectionName;

    public SchemaTokenAnalyzerService(
        IAppVectorDbService vectorDb,
        ITokenCounter tokenCounter,
        IConfiguration configuration,
        ILogger<SchemaTokenAnalyzerService> logger)
    {
        _vectorDb = vectorDb;
        _tokenCounter = tokenCounter;
        _logger = logger;
        _collectionName = configuration["Qdrant:CollectionName"]
            ?? "erp_knowledge_base_v3_indexed";
    }

    /// <inheritdoc />
    public async Task<SchemaTokenAnalysisResult> AnalyzeFullSchemaTokensAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║        [Schema Token Analyzer] STARTING FULL ANALYSIS                 ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new SchemaTokenAnalysisResult();

        // ═══════════════════════════════════════════════════
        // STEP 1: Pull ALL data from Qdrant by table_name
        // ═══════════════════════════════════════════════════
        var tablesToScan = new[]
        {
            DbTables.Pages,
            DbTables.PageAttribute,
            DbTables.PageDetail,
            DbTables.ListSetups,
            DbTables.Modul,
            DbTables.MenuMaster
        };

        var dataByTable = new Dictionary<string, List<string>>();

        foreach (var tableName in tablesToScan)
        {
            _logger.LogInformation("[SchemaTokenAnalyzer] 📥 Scrolling table: {TableName}...", tableName);

            var filter = new Dictionary<string, object>
            {
                [VectorDbMeta.SourceType] = VectorDbMeta.SourceTypeData,
                [VectorDbMeta.TableName] = tableName
            };

            var points = await _vectorDb.ScrollAsync(
                _collectionName,
                filter,
                limit: 10_000, // High limit to get ALL records
                ct: cancellationToken);

            // Extract original_text from each point's payload
            var texts = new List<string>();
            foreach (var point in points)
            {
                if (point.Payload.TryGetValue(VectorDbMeta.OriginalText, out var textValue))
                {
                    texts.Add(textValue.StringValue);
                }
            }

            dataByTable[tableName] = texts;
            result.RecordCounts[tableName] = texts.Count;

            _logger.LogInformation("[SchemaTokenAnalyzer] ✓ {TableName}: {Count} records",
                tableName, texts.Count);
        }

        // Also fetch schema-type records (CREATE VIEW/TABLE scripts)
        _logger.LogInformation("[SchemaTokenAnalyzer] 📥 Scrolling schema scripts...");
        var schemaFilter = new Dictionary<string, object>
        {
            [VectorDbMeta.SourceType] = VectorDbMeta.SourceTypeSchema
        };

        var schemaPoints = await _vectorDb.ScrollAsync(
            _collectionName,
            schemaFilter,
            limit: 10_000,
            ct: cancellationToken);

        var schemaTexts = new List<string>();
        foreach (var point in schemaPoints)
        {
            if (point.Payload.TryGetValue(VectorDbMeta.OriginalText, out var textValue))
            {
                schemaTexts.Add(textValue.StringValue);
            }
        }
        dataByTable["SchemaScripts"] = schemaTexts;
        result.RecordCounts["SchemaScripts"] = schemaTexts.Count;

        _logger.LogInformation("[SchemaTokenAnalyzer] ✓ SchemaScripts: {Count} records",
            schemaTexts.Count);

        // ═══════════════════════════════════════════════════
        // STEP 2: Assemble the Global Cache text blob
        // ═══════════════════════════════════════════════════
        _logger.LogInformation("[SchemaTokenAnalyzer] 🔨 Assembling global cache text blob...");

        var globalBlob = new StringBuilder();

        // 2a. Static Rules (system instructions)
        var staticRules = SqlGenerationService.CachedStaticRules;
        globalBlob.AppendLine("=== SYSTEM INSTRUCTIONS (Static Rules) ===");
        globalBlob.AppendLine(staticRules);
        globalBlob.AppendLine();

        // 2b. Schema data by table
        foreach (var (tableName, texts) in dataByTable)
        {
            globalBlob.AppendLine($"=== {tableName} ({texts.Count} records) ===");
            foreach (var text in texts)
            {
                globalBlob.AppendLine(text);
            }
            globalBlob.AppendLine();
        }

        var fullText = globalBlob.ToString();
        result.TotalCharacters = fullText.Length;

        _logger.LogInformation("[SchemaTokenAnalyzer] 📏 Total assembled text: {Chars} characters",
            result.TotalCharacters);

        // ═══════════════════════════════════════════════════
        // STEP 3: Count tokens via Gemini API
        // ═══════════════════════════════════════════════════
        _logger.LogInformation("[SchemaTokenAnalyzer] 🔢 Counting tokens via Gemini API...");

        // 3a. Static Rules tokens
        result.StaticRulesTokens = await _tokenCounter.CountTokensAsync(
            staticRules, cancellationToken);
        result.Breakdown["StaticRules"] = result.StaticRulesTokens;

        _logger.LogInformation("[SchemaTokenAnalyzer] ✓ StaticRules: {Tokens} tokens",
            result.StaticRulesTokens);

        // 3b. Per-table token counts
        int schemaTokensTotal = 0;
        int dictionaryTokensTotal = 0;

        foreach (var (tableName, texts) in dataByTable)
        {
            if (texts.Count == 0)
            {
                result.Breakdown[tableName] = 0;
                continue;
            }

            var tableText = string.Join("\n", texts);
            var tableTokens = await _tokenCounter.CountTokensAsync(
                tableText, cancellationToken);

            result.Breakdown[tableName] = tableTokens;

            // Categorize: ListSetups = Dictionary, everything else = Schema
            if (tableName == DbTables.ListSetups)
            {
                dictionaryTokensTotal += tableTokens;
            }
            else
            {
                schemaTokensTotal += tableTokens;
            }

            _logger.LogInformation("[SchemaTokenAnalyzer] ✓ {TableName}: {Tokens} tokens",
                tableName, tableTokens);
        }

        result.SchemaTokens = schemaTokensTotal;
        result.DictionaryTokens = dictionaryTokensTotal;

        // 3c. Total tokens (count the full assembled blob for accuracy)
        result.TotalTokens = await _tokenCounter.CountTokensAsync(
            fullText, cancellationToken);

        // Preview (first 500 chars)
        result.RawTextPreview = fullText.Length > 500
            ? fullText[..500] + "..."
            : fullText;

        stopwatch.Stop();

        // ═══════════════════════════════════════════════════
        // STEP 4: Log the final report
        // ═══════════════════════════════════════════════════
        _logger.LogInformation("╔════════════════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║        [Schema Token Analyzer] ANALYSIS COMPLETE ✓                    ║");
        _logger.LogInformation("╠════════════════════════════════════════════════════════════════════════╣");
        _logger.LogInformation("║ Total Tokens:      {Total,10}                                        ║",
            result.TotalTokens);
        _logger.LogInformation("║ Gemini Limit:      {Limit,10}                                        ║",
            result.GeminiContextLimit);
        _logger.LogInformation("║ Usage:             {Usage,9}%                                        ║",
            result.UsagePercentage);
        _logger.LogInformation("║ Fits in Cache:     {Fits,10}                                        ║",
            result.FitsInSingleCache ? "✅ YES" : "❌ NO");
        _logger.LogInformation("║ Analysis Time:     {Time,8}ms                                        ║",
            stopwatch.ElapsedMilliseconds);
        _logger.LogInformation("╚════════════════════════════════════════════════════════════════════════╝");

        foreach (var (table, tokens) in result.Breakdown.OrderByDescending(x => x.Value))
        {
            _logger.LogInformation("  📊 {Table,-20} {Tokens,8} tokens ({Records} records)",
                table, tokens, result.RecordCounts.GetValueOrDefault(table));
        }

        return result;
    }
}
