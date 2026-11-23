using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;

namespace SchemaGenerator.Services;

/// <summary>
/// التنفيذ الفعلي لـ ISchemaReaderService.
/// يستخدم SMO (SQL Server Management Objects) لجلب الـ Scripts الدقيقة،
/// ويطبق "التقسيم الذكي" (Smart Chunking) بناءً على النقد.
/// </summary>
public class SchemaReaderService : ISchemaReaderService
{
    private readonly ILogger<SchemaReaderService> _logger;
    private readonly PipelineRunOptions _runOptions;

    public SchemaReaderService(
        ILogger<SchemaReaderService> logger,
        PipelineRunOptions? runOptions = null)
    {
        _logger = logger;
        _runOptions = runOptions ?? new PipelineRunOptions();
    }

    /// <summary>
    /// يستخرج الـ Scripts بشكل تعاودي (Recursive) لضمان الوصول للجداول الأساسية.
    /// </summary>
    public async Task<List<VectorDocument>> ExtractSchemasAsync(
        string connectionString,
        string dbName,
        List<string> initialObjectNames,
        List<string> ignoredTableNames)
    {
        var documents = new List<VectorDocument>();
        var processedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var objectsToProcess = new Queue<string>(initialObjectNames.Distinct(StringComparer.OrdinalIgnoreCase));

        await using var sqlConnection = new SqlConnection(connectionString);
        var serverConnection = new ServerConnection(sqlConnection);
        var server = new Server(serverConnection);
        var db = server.Databases[dbName];

        var options = new ScriptingOptions
        {
            ScriptDrops = false,
            Indexes = true,
            DriAllConstraints = true,
            NoCollation = false,
            ScriptSchema = true,
            ScriptData = false
        };

        _logger.LogInformation("[SchemaReader] Starting recursive schema extraction...");

        while (objectsToProcess.Count > 0)
        {
            var objectName = objectsToProcess.Dequeue();

            if (processedObjects.Contains(objectName) ||
                (db.Tables.Contains(objectName) && db.Tables[objectName].IsSystemObject) ||
                (db.Views.Contains(objectName) && db.Views[objectName].IsSystemObject))
            {
                continue;
            }

            if (db.Views.Contains(objectName))
            {
                var view = db.Views[objectName];
                _logger.LogInformation("[SchemaReader] Processing View: '{ObjectName}'", objectName);

                var script = string.Join("\n", view.Script(options).Cast<string>());
                documents.Add(CreateVectorDocument(view.Name, script, "View", view.Name));
                processedObjects.Add(objectName);

                var dependencies = ParseTablesFromViewScript(view.TextBody);
                _logger.LogInformation("[SchemaReader]   -> Found dependencies: {Dependencies}", string.Join(", ", dependencies));
                foreach (var dep in dependencies)
                {
                    if (!processedObjects.Contains(dep))
                    {
                        objectsToProcess.Enqueue(dep);
                    }
                }
            }
            else if (db.Tables.Contains(objectName))
            {
                var table = db.Tables[objectName];
                _logger.LogInformation("[SchemaReader] Processing Table: '{ObjectName}'", objectName);
                documents.AddRange(ChunkTableScript(table, options));
                processedObjects.Add(objectName);
            }
            else
            {
                _logger.LogWarning("[SchemaReader] Warning: Object '{ObjectName}' not found as a table or view. Skipping.", objectName);
                processedObjects.Add(objectName);
            }
        }

        _logger.LogInformation("[SchemaReader] Recursive extraction finished. Processed {Count} unique objects.", processedObjects.Count);

        _logger.LogInformation("\n[SchemaReader] --- Summary of All Processed Schema Objects ---");
        foreach (var processedObject in processedObjects.OrderBy(x => x))
        {
            _logger.LogInformation("  - {ProcessedObject}", processedObject);
        }
        _logger.LogInformation("-------------------------------------------------");

        return documents;
    }

    private List<VectorDocument> ChunkTableScript(Table table, ScriptingOptions options)
    {
        var docList = new List<VectorDocument>();
        string script = string.Join("\n", table.Script(options).Cast<string>());

        _logger.LogDebug("[SchemaReader] Generated script for Table '{TableName}':\n{Script}", table.Name, script);

        docList.Add(CreateVectorDocument(table.Name, script, "Table", table.Name));
        return docList;
    }

    private static VectorDocument CreateVectorDocument(string id, string script, string type, string name)
    {
        return new VectorDocument
        {
            DocumentId = $"Schema_{type}_{id}",
            TextContent = script,
            IsDeleted = false,
            Metadata = new Dictionary<string, string>
            {
                { "source_type", "schema" },
                { "object_type", type },
                { "object_name", name },
                { "tenant_id", "ALL" }
            }
        };
    }

    private List<string> ParseTablesFromViewScript(string script)
    {
        TSqlParser parser = new TSql160Parser(false);
        using var reader = new StringReader(script);
        TSqlFragment fragment = parser.Parse(reader, out var parseErrors);

        if (fragment == null || (parseErrors != null && parseErrors.Count > 0))
        {
            _logger.LogError("[SchemaReader] ERROR: TSqlParser FAILED to analyze a View script.");
            _logger.LogError("[SchemaReader] View (First 150 chars): {Snippet}...", script.Substring(0, Math.Min(150, script.Length)));

            if (parseErrors != null)
            {
                foreach (var error in parseErrors)
                {
                    _logger.LogError("[SchemaReader] Parse Error: {Message} (Line: {Line}, Column: {Column})", error.Message, error.Line, error.Column);
                }
            }

            if (_runOptions.AutoConfirm)
            {
                _logger.LogWarning("[SchemaReader] Auto-confirm (--yes/-y) enabled. Skipping table discovery for this view and continuing.");
                return new List<string>();
            }

            if (_runOptions.NonInteractive || Console.IsInputRedirected)
            {
                throw new InvalidOperationException(
                    "TSqlParser failed to analyze a view script in non-interactive mode. Pass --yes/-y to continue on parse errors.");
            }

            _logger.LogWarning("This error means the tables *inside* this View were NOT found.");
            _logger.LogWarning("  - (C) Continue: Ignore this view's tables and proceed with the pipeline.");
            _logger.LogWarning("  - (S) Stop: Abort the entire pipeline (safest option).");

            while (true)
            {
                Console.Write("  Enter your choice (C / S): ");
                string? response = Console.ReadLine()?.Trim().ToUpperInvariant();

                if (response == "S" || response == "STOP")
                {
                    throw new InvalidOperationException("User aborted pipeline due to TSqlParser failure on view. See errors above.");
                }
                if (response == "C" || response == "CONTINUE")
                {
                    _logger.LogWarning("[SchemaReader] User chose to continue. Skipping table discovery for this view...");
                    return new List<string>();
                }

                _logger.LogWarning("Invalid input. Please enter 'C' or 'S'.");
            }
        }

        var visitor = new TableVisitor();
        fragment.Accept(visitor);

        _logger.LogInformation("[SchemaReader] Successfully parsed view. Found tables: {Tables}", string.Join(", ", visitor.TableNames.Distinct()));
        return visitor.TableNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<Dictionary<string, DateTime>> GetObjectsModificationDatesAsync(string connectionString, List<string> objectNames)
    {
        await using var connection = new SqlConnection(connectionString);

        const string query = @"
            SELECT name, modify_date 
            FROM sys.objects 
            WHERE name IN @Names";

        var results = await connection.QueryAsync<(string Name, DateTime ModifyDate)>(
            query,
            new { Names = objectNames });

        return results.ToDictionary(x => x.Name, x => x.ModifyDate);
    }

    private class TableVisitor : TSqlFragmentVisitor
    {
        public List<string> TableNames { get; } = new();

        public override void ExplicitVisit(NamedTableReference node)
        {
            if (node.SchemaObject?.BaseIdentifier?.Value != null)
            {
                TableNames.Add(node.SchemaObject.BaseIdentifier.Value);
            }
        }
    }
}
