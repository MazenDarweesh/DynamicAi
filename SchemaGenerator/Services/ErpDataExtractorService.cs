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
        private readonly string _stateFilePath; // (سنحصل عليه من Orchestrator لاحقاً)

        public ErpDataExtractorService(IChangeTrackingStrategy trackingStrategy, IStateService stateService)
        {
            _trackingStrategy = trackingStrategy;
            _stateService = stateService;
            _stateFilePath = "Infrastructure/process_state.json"; // (تحديد المسار الثابت)
        }

        /// <summary>
        /// يستخرج التغييرات من قائمة جداول البيانات ويحولها إلى VectorDocuments.
        /// </summary>
        public async Task<List<VectorDocument>> ExtractDataChangesAsync(
            string connectionString,
            string dbName,
            IEnumerable<string> tableNames)
        {
            var allDocuments = new List<VectorDocument>();

            foreach (var tableName in tableNames)
            {
                // 1. جلب "آخر نقطة توقف" (LSN) من الذاكرة (اليوم 2)
                string lastLsnString = await _stateService.GetLastLsnForTable(_stateFilePath, tableName);
                byte[] lastLsnBytes = lastLsnString != null ?
                    Convert.FromHexString(lastLsnString.Substring(2)) : // (تحويل "0xABC" إلى byte[])
                    null;

                // 2. استدعاء "محرك" الـ CDC (الذي بنيناه في المهمة 2)
                var cdcResult = await _trackingStrategy.GetChangesAsync(connectionString, dbName, tableName, lastLsnBytes);

                // 3. تحويل (Transform) التغييرات إلى VectorDocuments
                foreach (var row in cdcResult.Changes)
                {
                    allDocuments.Add(TransformRowToDocument(row, tableName));
                }

                // 4. (الأهم) تحديث "الذاكرة" بآخر LSN وصلنا إليه
                string newLsnString = "0x" + Convert.ToHexString(cdcResult.NewLsnBytes);
                await _stateService.SetLastLsnForTable(_stateFilePath, tableName, newLsnString);
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