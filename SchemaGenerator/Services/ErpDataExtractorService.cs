using Dapper;
using SchemaGenerator.Common;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using SchemaGenerator.Models.Erp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
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
        private readonly IStateService _stateService;
        private readonly JsonSerializerOptions _jsonOptions;

        public ErpDataExtractorService(IStateService stateService)
        {
            _stateService = stateService;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
        }

        /// <summary>
        /// (مُعدل بالكامل) يقوم باستخراج البيانات الوصفية الموجهة بناءً على سياق الصفحات.
        /// </summary>
        public async Task<List<VectorDocument>> ExtractDataChangesAsync(
            string connectionString,
            string dbName,
            Dictionary<string, ErpPageContext> contextMap,
            string stateFilePath)
        {
            var allDocuments = new List<VectorDocument>();
            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ChangeDatabaseAsync(dbName);

            if (!contextMap.Any())
            {
                Console.WriteLine("[DataExtractor] Context map is empty, skipping data extraction.");
                return allDocuments;
            }

            Console.WriteLine("[DataExtractor] Starting targeted metadata extraction...");

            var allContexts = contextMap.Values;
            var targetPageIds = allContexts.Select(c => c.PageId).Distinct().ToList();
            var masterListNames = allContexts.Select(c => c.ListName).Where(n => !string.IsNullOrEmpty(n));
            var detailListNames = allContexts.SelectMany(c => c.DetailContexts).Select(d => d.ListName).Where(n => !string.IsNullOrEmpty(n));
            var allListNames = masterListNames.Concat(detailListNames).Distinct().ToList();

            var pages = await FetchDataAsync(connection, DbTables.Pages, DbColumns.PageID_Upper, targetPageIds);
            allDocuments.AddRange(pages.Select(row => TransformRowToDocument(row, DbTables.Pages)));

            var pageAttributes = await FetchDataAsync(connection, DbTables.PageAttribute, DbColumns.PageId_Lower, targetPageIds);
            allDocuments.AddRange(pageAttributes.Select(row => TransformRowToDocument(row, DbTables.PageAttribute)));

            // --- (بداية التعديل الجذري) ---
            // المرور على كل PageAttribute لجلب التفاصيل المرتبطة به وتمرير سياق الـ ID
            var attributesById = pageAttributes.ToDictionary(pa => (int)pa[DbColumns.AttributeId]);
            foreach (var attrId in attributesById.Keys)
            {
                var pageDetails = await FetchDataAsync(connection, DbTables.PageDetail, DbColumns.PageAttributeId, new List<int> { attrId });
                allDocuments.AddRange(pageDetails.Select(row => TransformRowToDocument(row, DbTables.PageDetail, attributeId: attrId)));
            }

            // المرور على كل Page لجلب الموديول والقائمة الرئيسية وتمرير سياق الـ ID
            var pagesById = pages.ToDictionary(p => (int)p[DbColumns.PageID_Upper]);
            foreach (var pageId in pagesById.Keys)
            {
                var page = pagesById[pageId];
                if (page.TryGetValue(DbColumns.ModulID_Upper, out var modulIdObj) && modulIdObj != null)
                {
                    var moduls = await FetchDataAsync(connection, DbTables.Modul, DbColumns.MID, new List<int> { (int)modulIdObj });
                    allDocuments.AddRange(moduls.Select(row => TransformRowToDocument(row, DbTables.Modul, pageId: pageId)));
                }
                if (page.TryGetValue(DbColumns.MasterID_Upper, out var menuMasterIdObj) && menuMasterIdObj != null)
                {
                    var menuMasters = await FetchDataAsync(connection, DbTables.MenuMaster, DbColumns.MID, new List<int> { (int)menuMasterIdObj });
                    allDocuments.AddRange(menuMasters.Select(row => TransformRowToDocument(row, DbTables.MenuMaster, pageId: pageId)));
                }
            }
            // --- (نهاية التعديل الجذري) ---

            if (allListNames.Any())
            {
                var listSetups = await FetchDataAsync(connection, DbTables.ListSetups, DbColumns.ListName, allListNames);
                allDocuments.AddRange(listSetups.Select(row => TransformRowToDocument(row, DbTables.ListSetups)));
            }

            Console.WriteLine($"[DataExtractor] Targeted extraction complete. Found {allDocuments.Count} relevant metadata rows.");
            return allDocuments;
        }

        private async Task<List<Dictionary<string, object>>> FetchDataAsync<T>(Microsoft.Data.SqlClient.SqlConnection connection, string tableName, string columnName, List<T> ids)
        {
            if (ids == null || !ids.Any()) return new List<Dictionary<string, object>>();

            Console.WriteLine($"  - Fetching from '{tableName}' where '{columnName}' IN ({string.Join(",", ids.Take(5))}{(ids.Count > 5 ? "..." : "")})");
            var query = $"SELECT * FROM dbo.[{tableName}] WHERE [{columnName}] IN @Ids";
            var rows = await connection.QueryAsync(query, new { Ids = ids });
            return rows.Select(row => ((IDictionary<string, object>)row).ToDictionary(kvp => kvp.Key, kvp => kvp.Value)).ToList();
        }

        private VectorDocument TransformRowToDocument(Dictionary<string, object> row, string tableName, int? pageId = null, int? attributeId = null)
        {
            string rowAsJson = JsonSerializer.Serialize(row, _jsonOptions);
            string rowId = row.Values.FirstOrDefault()?.ToString() ?? Guid.NewGuid().ToString();

            // Ensure context-specific rows (e.g., Modul/MenuMaster tied to PageID) remain unique per relationship.
            // Otherwise, reusing the same DocumentId would overwrite the previous page/context metadata in Qdrant.
            var contextSuffixBuilder = new StringBuilder();
            if (pageId.HasValue)
            {
                contextSuffixBuilder.Append($"_P{pageId.Value}");
            }
            if (attributeId.HasValue)
            {
                contextSuffixBuilder.Append($"_A{attributeId.Value}");
            }
            string contextSuffix = contextSuffixBuilder.ToString();

            var metadata = new Dictionary<string, string>
            {
                { VectorDbMeta.SourceType, VectorDbMeta.SourceTypeData },
                { VectorDbMeta.TableName, tableName },
                { VectorDbMeta.RowId, rowId },
                { VectorDbMeta.TenantId, VectorDbMeta.TenantAll }
            };

            // --- (منطق الربط المحسّن) ---
            // 1. استخدم الـ ID الممرر بشكل صريح (لأنه يمثل العلاقة)
            if (pageId.HasValue)
            {
                metadata[VectorDbMeta.PageId] = pageId.Value.ToString();
            }
            // 2. إذا لم يوجد، ابحث عنه في بيانات الصف نفسه
            else if (row.TryGetValue(DbColumns.PageID_Upper, out var pageIdValue) || row.TryGetValue(DbColumns.PageId_Lower, out pageIdValue))
            {
                if (pageIdValue != null) metadata[VectorDbMeta.PageId] = pageIdValue.ToString();
            }

            if (attributeId.HasValue)
            {
                metadata[VectorDbMeta.AttributeId] = attributeId.Value.ToString();
            }
            else if (row.TryGetValue(DbColumns.AttributeId, out var attrIdValue) && attrIdValue != null)
            {
                metadata[VectorDbMeta.AttributeId] = attrIdValue.ToString();
            }
            // --- (نهاية منطق الربط) ---

            if (row.TryGetValue(DbColumns.DetailId, out var detailIdValue) && detailIdValue != null)
            {
                metadata[VectorDbMeta.DetailId] = detailIdValue.ToString();
            }
            if (row.TryGetValue(DbColumns.ListName, out var listNameValue) && listNameValue != null)
            {
                metadata[VectorDbMeta.ListName] = listNameValue.ToString();
            }
            if (row.TryGetValue(DbColumns.PageDisplay, out var pageDisplayValue) && pageDisplayValue != null)
            {
                metadata[VectorDbMeta.PageName] = pageDisplayValue.ToString();
            }

            return new VectorDocument
            {
                DocumentId = $"Data_{tableName}_{rowId}{contextSuffix}",
                TextContent = rowAsJson,
                IsDeleted = false,
                Metadata = metadata
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