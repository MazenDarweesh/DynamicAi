using Dapper;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace SchemaGenerator.Services
{
    /// <summary>
    /// التنفيذ الفعلي لـ IErpDataExtractorService.
    /// هو "المدير" الذي يربط بين (IStateService) و (IChangeTrackingStrategy)
    /// ليقوم بتحويل "التغييرات" إلى "مستندات Vector" كاملة.
    /// </summary>
    public class ErpDataExtractorService : IErpDataExtractorService
    {
        private readonly IChangeTrackingStrategy _trackingStrategy;
        private readonly IStateService _stateService;
        // (جديد) سنحتاج Query بسيط للـ Initial Load
        private readonly JsonSerializerOptions _jsonOptions;

        public ErpDataExtractorService(IChangeTrackingStrategy trackingStrategy, IStateService stateService)
        {
            _trackingStrategy = trackingStrategy;
            _stateService = stateService;
            // (جديد) إعدادات لضمان تحويل الأعمدة بأسماء صحيحة
            _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        }

        /// <summary>
        /// (مُعدل) أصبح أذكى ليتعامل مع "التشغيل الأولي"
        /// </summary>
        public async Task<List<VectorDocument>> ExtractDataChangesAsync(
            string connectionString,
            string dbName,
            IEnumerable<string> tableNames,
            string stateFilePath)
        {
            var allDocuments = new List<VectorDocument>();
            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ChangeDatabaseAsync(dbName);

            // (جديد) الحصول على الـ LSN "الحالي" *قبل* البدء
            byte[] currentLsn = await connection.QuerySingleAsync<byte[]>("SELECT sys.fn_cdc_get_max_lsn()");

            foreach (var tableName in tableNames)
            {
                string lastLsnString = await _stateService.GetLastLsnForTable(stateFilePath, tableName);
                List<Dictionary<string, object>> changedRows;

                if (lastLsnString == null)
                {
                    // --- (1) منطق التشغيل الأولي (Initial Load) ---
                    Console.WriteLine($"[DataExtractor] First run for '{tableName}'. Performing full table load (SELECT *)...");

                    // (أ) جلب كل البيانات
                    var rows = await connection.QueryAsync($"SELECT * FROM dbo.[{tableName}]");

                    // (ب) تحويلها
                    changedRows = rows
                        .Select(row => (IDictionary<string, object>)row)
                        .Select(dict =>
                        {
                            // (نضيف علامة "إدخال" يدوياً)
                            dict["__$operation"] = 2; // (2 = INSERT)
                            return dict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                        })
                        .Cast<Dictionary<string, object>>()
                        .ToList();

                    Console.WriteLine($"[DataExtractor] Found {changedRows.Count} initial rows in '{tableName}'.");
                }
                else
                {
                    // --- (2) منطق التحديث المستمر (Delta Update) ---
                    byte[] lastLsnBytes = Convert.FromHexString(lastLsnString.Substring(2));

                    // (استدعاء محرك CDC الذي بنيناه)
                    var cdcResult = await _trackingStrategy.GetChangesAsync(connectionString, dbName, tableName, lastLsnBytes);

                    // (ملاحظة: الـ currentLsn من cdcResult هو نفسه الذي حسبناه في الأعلى)
                    changedRows = cdcResult.Changes;
                }

                // --- (3) التحويل (Transform) المشترك ---
                foreach (var row in changedRows)
                {
                    allDocuments.Add(TransformRowToDocument(row, tableName));
                }

                // --- (4) تحديث الذاكرة ---
                // (سنقوم بتحديث الـ LSN في كلتا الحالتين لضمان أننا نبدأ من النقطة الصحيحة في المرة القادمة)
                string newLsnString = "0x" + Convert.ToHexString(currentLsn);
                await _stateService.SetLastLsnForTable(stateFilePath, tableName, newLsnString);
            }

            return allDocuments;
        }

        /// <summary>
        /// دالة مساعدة لتحويل الصف (Row) إلى مستند VectorDocument (كما اقترح النقد #2).
        /// </summary>
        private VectorDocument TransformRowToDocument(Dictionary<string, object> row, string tableName)
        {
            // فصل بيانات CDC عن بيانات الصف
            int operation = (int)row["__$operation"];
            row.Remove("__$operation");

            // (هام) تحويل الصف بالكامل إلى نص JSON
            string rowAsJson = JsonSerializer.Serialize(row);

            // (هام) استخراج الـ ID الرئيسي للصف (نفترض أنه أول عمود)
            string rowId = row.Values.First()?.ToString() ?? Guid.NewGuid().ToString();

            return new VectorDocument
            {
                DocumentId = $"Data_{tableName}_{rowId}",
                TextContent = rowAsJson, // (هذا ما سيتم عمل Embedding له)
                IsDeleted = (operation == 1), // (عملية 1 = Delete)

                // (الـ Metadata الغنية للفلترة السريعة)
                Metadata = new Dictionary<string, string>
                {
                    { "source_type", "data" },
                    { "table_name", tableName },
                    { "row_id", rowId },
                    { "cdc_operation", operation.ToString() },
                    { "tenant_id", "ALL" } // (أو استخراجه من الصف إذا كان متاحاً)
                }
            };
        }
    }
}
/*
* ==========================================================================================
* SERVICE SUMMARY (With Examples)
* ==========================================================================================
* Input (Example):  dbName = "QualityControl"
* tableNames = ["ListSetups", "Pages", "PageAttribute", ...]
*
* Output (Example): Task<List<VectorDocument>> (A list of documents representing changed rows)
* Example Entry:
* { DocumentId: "Data_ListSetups_38980", 
* TextContent: "{\"id\":38980, \"ListName\":\"SalestView\", \"arColumnLabel\":\"رقم العقد\"...}",
* IsDeleted: false,
* Metadata: {"source_type":"data", "table_name":"ListSetups", "row_id":"38980"} }
*
* Called By:        SchemaOrchestrator.cs
*
* Benefit:          This is the "Data Extraction Manager". It orchestrates the
* entire data pipeline by:
* 1. Getting the last known LSN from IStateService.
* 2. Calling IChangeTrackingStrategy (CdcChangeStrategy) to fetch *only* the changed rows.
* 3. "Transforming" each changed row into a final VectorDocument 
* (JSON text + Rich Metadata payload) ready for Qdrant.
* 4. Updating IStateService with the new LSN.
*/