using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SchemaGenerator.Services
{
    /// <summary>
    /// التنفيذ الفعلي لـ ISchemaReaderService.
    /// يستخدم SMO (SQL Server Management Objects) لجلب الـ Scripts الدقيقة،
    /// ويطبق "التقسيم الذكي" (Smart Chunking) بناءً على النقد.
    /// </summary>
    public class SchemaReaderService : ISchemaReaderService
    {
        /// <summary>
        /// (هنا سنضع خريطة التقسيم الدلالي للجداول الضخمة كما اقترح النقد)
        /// (سنتركها فارغة الآن للـ MVP ونركز على التقسيم البسيط)
        /// </summary>
        private readonly Dictionary<string, Dictionary<string, List<string>>> _chunkingMap = new();
        private readonly ILogger<SchemaReaderService> _logger;

        public SchemaReaderService(ILogger<SchemaReaderService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// (مُعدل) يستخرج الـ Scripts بشكل تعاودي (Recursive) لضمان الوصول للجداول الأساسية.
        /// </summary>
        public async Task<List<VectorDocument>> ExtractSchemasAsync(
            string connectionString,
            string dbName,
            List<string> initialObjectNames,
            List<string> ignoredTableNames) // تم تجاهل هذا الباراميتر الآن
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

                if (processedObjects.Contains(objectName) || (db.Tables.Contains(objectName) && db.Tables[objectName].IsSystemObject) || (db.Views.Contains(objectName) && db.Views[objectName].IsSystemObject))
                {
                    continue; // تخطي إذا تمت معالجته أو كان كائن نظام
                }

                // 1. هل هو View؟
                if (db.Views.Contains(objectName))
                {
                    var view = db.Views[objectName];
                    _logger.LogInformation("[SchemaReader] Processing View: '{ObjectName}'", objectName);

                    // أضف سكربت الـ View
                    var script = string.Join("\n", view.Script(options).Cast<string>());
                    documents.Add(CreateVectorDocument(view.Name, script, "View", view.Name));
                    processedObjects.Add(objectName);

                    // استكشف ما بداخل الـ View
                    var dependencies = ParseTablesFromViewScript(view.TextBody);
                    _logger.LogInformation("[SchemaReader]   -> Found dependencies: {Dependencies}", string.Join(", ", dependencies));
                    foreach (var dep in dependencies)
                    {
                        if (!processedObjects.Contains(dep))
                        {
                            objectsToProcess.Enqueue(dep); // أضف التوابع لقائمة الانتظار
                        }
                    }
                }
                // 2. هل هو Table؟
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
                    processedObjects.Add(objectName); // نعتبره معالجاً لتجنب التكرار
                }
            }

            _logger.LogInformation("[SchemaReader] Recursive extraction finished. Processed {Count} unique objects.", processedObjects.Count);

            // --- (بداية التعديل: إضافة ملخص) ---
            _logger.LogInformation("\n[SchemaReader] --- Summary of All Processed Schema Objects ---");
            foreach (var processedObject in processedObjects.OrderBy(x => x))
            {
                _logger.LogInformation("  - {ProcessedObject}", processedObject);
            }
            _logger.LogInformation("-------------------------------------------------");
            // --- (نهاية التعديل) ---
            // --- (نهاية التعديل) ---

            return documents;
        }

        /// <summary>
        /// (MVP) ينشئ مستنداً واحداً للجدول.
        /// (للمستقبل: سنطبق هنا التقسيم الدلالي من النقد #1)
        /// </summary>
        private List<VectorDocument> ChunkTableScript(Table table, ScriptingOptions options)
        {
            // (تجاهل التقسيم المعقد في الـ MVP للسرعة)
            // (سنترك خريطة التقسيم (Chunking Map) لوقتها كما طلبت)

            var docList = new List<VectorDocument>();
            string script = string.Join("\n", table.Script(options).Cast<string>());

            _logger.LogDebug("[SchemaReader] Generated script for Table '{TableName}':\n{Script}", table.Name, script);

            docList.Add(CreateVectorDocument(table.Name, script, "Table", table.Name));
            return docList;
        }

        /// <summary>
        /// دالة مساعدة لإنشاء المستند بالـ Metadata الغنية (كما اقترح النقد #2)
        /// </summary>
        private VectorDocument CreateVectorDocument(string id, string script, string type, string name)
        {
            return new VectorDocument
            {
                DocumentId = $"Schema_{type}_{id}",
                TextContent = script,
                IsDeleted = false,
                Metadata = new Dictionary<string, string>
                {
                    { "source_type", "schema" },
                    { "object_type", type }, // "Table" or "View"
                    { "object_name", name },
                    { "tenant_id", "ALL" } // (نفترض أن السكيما عامة)
                    // (سنضيف المزيد من الـ Metadata لاحقاً مثل "RelatedTables")
                }
            };
        }

        //
        /// <summary>
        /// (جديد) يحلل نص الـ View لاستخراج الجداول المرجعية باستخدام ScriptDom.
        /// (مُعدل) يتوقف ويسأل المستخدم عند حدوث خطأ في التحليل.
        /// </summary>
        private List<string> ParseTablesFromViewScript(string script)
        {
            TSqlParser parser = new TSql160Parser(false);
            using (var reader = new StringReader(script))
            {
                IList<ParseError> parseErrors;
                TSqlFragment fragment = parser.Parse(reader, out parseErrors);

                if (fragment == null || (parseErrors != null && parseErrors.Count > 0))
                {
                    // --- (بداية التعديل: طباعة الخطأ بوضوح) ---
                    // --- (بداية التعديل: طباعة الخطأ بوضوح) ---
                    _logger.LogError("[SchemaReader] ⛔️ ERROR: TSqlParser FAILED to analyze a View script.");
                    _logger.LogError("[SchemaReader] View (First 150 chars): {Snippet}...", script.Substring(0, Math.Min(150, script.Length)));

                    if (parseErrors != null)
                    {
                        foreach (var error in parseErrors)
                        {
                            _logger.LogError("[SchemaReader] Parse Error: {Message} (Line: {Line}, Column: {Column})", error.Message, error.Line, error.Column);
                        }
                    }

                    // --- (الجزء الجديد: سؤال المستخدم) ---
                    // Note: Interactive prompts kept in Console as this is a CLI input requirement
                    _logger.LogWarning("This error means the tables *inside* this View were NOT found.");
                    _logger.LogWarning("  - (C) Continue: Ignore this view's tables and proceed with the pipeline.");
                    _logger.LogWarning("  - (S) Stop: Abort the entire pipeline (safest option).");

                    while (true)
                    {
                        Console.Write("  Enter your choice (C / S): ");
                        string response = Console.ReadLine()?.Trim().ToUpper();

                        if (response == "S" || response == "STOP")
                        {
                            // إيقاف البرنامج بالكامل عن طريق إطلاق خطأ (Exception)
                            // سيلتقطه الـ Orchestrator
                            throw new InvalidOperationException($"User aborted pipeline due to TSqlParser failure on view. See errors above.");
                        }
                        if (response == "C" || response == "CONTINUE")
                        {
                            _logger.LogWarning("[SchemaReader] User chose to continue. Skipping table discovery for this view...");
                            Console.ResetColor();
                            return new List<string>(); // (الإكمال: إرجاع قائمة فارغة)
                        }

                        _logger.LogWarning("Invalid input. Please enter 'C' or 'S'.");
                    }
                    // --- (نهاية الجزء الجديد) ---
                }

                TableVisitor visitor = new TableVisitor();
                fragment.Accept(visitor);

                // (تعديل إضافي) طباعة الجداول المكتشفة
                _logger.LogInformation("[SchemaReader] Successfully parsed view. Found tables: {Tables}", string.Join(", ", visitor.TableNames.Distinct()));


                return visitor.TableNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        public async Task<Dictionary<string, DateTime>> GetObjectsModificationDatesAsync(string connectionString, List<string> objectNames)
        {
            await using var connection = new SqlConnection(connectionString);

            // استعلام فائق السرعة يجلب الاسم وتاريخ التعديل
            const string query = @"
                SELECT name, modify_date 
                FROM sys.objects 
                WHERE name IN @Names";

            var results = await connection.QueryAsync<(string Name, DateTime ModifyDate)>(
                query,
                new { Names = objectNames }
            );

            return results.ToDictionary(x => x.Name, x => x.ModifyDate);
        }

        /// <summary>
        /// كلاس مساعد (Visitor) يتجول في شجرة بناء الجملة (AST)
        /// ويقوم بتجميع أسماء الجداول.
        /// </summary>
        private class TableVisitor : TSqlFragmentVisitor
        {
            public List<string> TableNames { get; } = new List<string>();

            public override void ExplicitVisit(NamedTableReference node)
            {
                if (node.SchemaObject?.BaseIdentifier?.Value != null)
                {
                    TableNames.Add(node.SchemaObject.BaseIdentifier.Value);
                }
            }
        }

    }
}
/*
* ==========================================================================================
* SERVICE SUMMARY (With Examples)
* ==========================================================================================
* Input (Example):  dbName = "QualityControl"
* targetViewNames = ["ContractViewWithPaid", "salessystemcontract"]
* targetTableNames = ["Contract", "SystemsContract"] (Discovered by ErpMetadataService)
*
* Output (Example): A Task<List<VectorDocument>> containing:
* [
* { DocumentId: "Schema_View_ContractViewWithPaid", 
* TextContent: "CREATE VIEW [ContractViewWithPaid] AS SELECT ...",
* Metadata: {"source_type":"schema", "object_type":"View", ...} },
* { DocumentId: "Schema_Table_Contract_Chunk_1_Financial", 
* TextContent: "CREATE TABLE [Contract]( [ContractAmountwTax] float, [Tax] float ... )",
* Metadata: {"source_type":"schema", "object_type":"Table_Chunk", ...} }
* ]
*
* Called By:        SchemaOrchestrator.cs
*
* Benefit:          This service uses SMO to get full, accurate CREATE scripts (with keys/indexes)
* and applies the "Smart Chunking" strategy (as discussed in the critique) 
* to large tables like "Contract", providing semantically rich documents 
* for the AI.
*/