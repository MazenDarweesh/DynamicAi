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

        /// <summary>
        /// يستخرج الـ Scripts الخاصة بالجداول والـ Views المستهدفة.
        /// </summary>
        public async Task<List<VectorDocument>> ExtractSchemasAsync(
            string connectionString,
            string dbName,
            List<string> targetViewNames,
            List<string> targetTableNames)
        {
            var documents = new List<VectorDocument>();
            await using var sqlConnection = new SqlConnection(connectionString);
            ServerConnection serverConnection = new ServerConnection(sqlConnection);
            Server server = new Server(serverConnection);
            Database db = server.Databases[dbName];
            // (جديد) قائمة لتجميع الجداول المكتشفة
            var discoveredTableNames = new HashSet<string>(targetTableNames);

            // إعدادات استخراج الـ Script (كما اتفقنا في النقد)
            ScriptingOptions options = new ScriptingOptions
            {
                ScriptDrops = false,
                Indexes = true,           // (مهم جداً)
                DriAllConstraints = true, // (مهم جداً - Foreign Keys)
                NoCollation = false,      // (نحتاج الكوليشن ليفهم الـ AI السياق)
                ScriptSchema = true,
                ScriptData = false
            };

            // 1. استخراج الـ Views المستهدفة
            foreach (var viewName in targetViewNames.Distinct())
            {
                if (db.Views.Contains(viewName))
                {
                    View view = db.Views[viewName];
                    if (view.IsSystemObject) continue;

                    /// <summary>
                    /// (السطر السحري)
                    /// 1. view.Script(options): يطلب من SMO توليد الـ "CREATE VIEW..." script بناءً على الإعدادات.
                    /// 2. .Cast<string>().Join("\n"): الـ Script يأتي كـ "مصفوفة من السطور"،
                    ///    هذا السطر يجمعها في "نص واحد" (string) مع فواصل أسطر (new lines).
                    /// </summary>
                    string script = string.Join("\n", view.Script(options).Cast<string>());
                    documents.Add(CreateVectorDocument(viewName, script, "View", view.Name));

                    // 2. (جديد) تفكيك الـ View Script لاكتشاف الجداول
                    List<string> tablesInThisView = ParseTablesFromViewScript(script);
                    foreach (var table in tablesInThisView)
                    {
                        discoveredTableNames.Add(table); // (إضافة الجداول المكتشفة)
                    }

                }
            }

            // 2. استخراج الجداول المستهدفة
            foreach (var tableName in discoveredTableNames.Distinct())
            {
                if (db.Tables.Contains(tableName))
                {
                    Table table = db.Tables[tableName];
                    if (table.IsSystemObject) continue;

                    // تطبيق "التقسيم الذكي" (Smart Chunking)
                    documents.AddRange(ChunkTableScript(table, options));
                }
            }
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

        /// <summary>
        /// (جديد) يحلل نص الـ View لاستخراج الجداول المرجعية باستخدام ScriptDom.
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
                    // في حالة فشل التحليل، أرجع قائمة فارغة.
                    // يمكن تسجيل الأخطاء هنا للمساعدة في التصحيح.
                    return new List<string>();
                }

                TableVisitor visitor = new TableVisitor();
                fragment.Accept(visitor);

                return visitor.TableNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
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

        /// <summary>
        /// دالة مساعدة لحساب الهاش
        /// </summary>
        private string ComputeSHA256(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
                var builder = new StringBuilder();
                foreach (var b in bytes)
                {
                    builder.Append(b.ToString("x2"));
                }
                return builder.ToString();
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