using Dapper;
using SchemaGenerator.Common;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using SchemaGenerator.Models.Erp;
using System;
using System.Collections.Generic;
using System.Linq;
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

            // 1. استخراج IDs و ListNames من السياق
            var targetPageIds = contextMap.Values.Select(c => c.PageId).Distinct().ToList();
            var allContexts = contextMap.Values;
            var masterListNames = allContexts.Select(c => c.ListName).Where(n => !string.IsNullOrEmpty(n));
            var detailListNames = allContexts.SelectMany(c => c.DetailContexts).Select(d => d.ListName).Where(n => !string.IsNullOrEmpty(n));
            var allListNames = masterListNames.Concat(detailListNames).Distinct().ToList();

            // 2. استخراج البيانات من الجداول الستة بشكل موجه
            var pages = await FetchDataAsync(connection, DbTables.Pages, DbColumns.PageID_Upper, targetPageIds);
            allDocuments.AddRange(pages.Select(row => TransformRowToDocument(row, DbTables.Pages)));

            var pageAttributes = await FetchDataAsync(connection, DbTables.PageAttribute, DbColumns.PageId_Lower, targetPageIds);
            allDocuments.AddRange(pageAttributes.Select(row => TransformRowToDocument(row, DbTables.PageAttribute)));

            var attributeIds = pageAttributes.Select(pa => (int)pa[DbColumns.AttributeId]).Distinct().ToList();
            if (attributeIds.Any())
            {
                var pageDetails = await FetchDataAsync(connection, DbTables.PageDetail, DbColumns.PageAttributeId, attributeIds);
                allDocuments.AddRange(pageDetails.Select(row => TransformRowToDocument(row, DbTables.PageDetail)));
            }

            var modulIds = pages.Where(p => p.ContainsKey(DbColumns.ModulID_Upper) && p[DbColumns.ModulID_Upper] != null).Select(p => (int)p[DbColumns.ModulID_Upper]).Distinct().ToList();
            if (modulIds.Any())
            {
                var moduls = await FetchDataAsync(connection, DbTables.Modul, DbColumns.MID, modulIds);
                allDocuments.AddRange(moduls.Select(row => TransformRowToDocument(row, DbTables.Modul)));
            }

            var menuMasterIds = pages.Where(p => p.ContainsKey(DbColumns.MasterID_Upper) && p[DbColumns.MasterID_Upper] != null).Select(p => (int)p[DbColumns.MasterID_Upper]).Distinct().ToList();
            if (menuMasterIds.Any())
            {
                var menuMasters = await FetchDataAsync(connection, DbTables.MenuMaster, DbColumns.MID, menuMasterIds);
                allDocuments.AddRange(menuMasters.Select(row => TransformRowToDocument(row, DbTables.MenuMaster)));
            }

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

            Console.WriteLine($"  - Fetching from '{tableName}' where '{columnName}' IN (...)");
            var query = $"SELECT * FROM dbo.[{tableName}] WHERE [{columnName}] IN @Ids";
            var rows = await connection.QueryAsync(query, new { Ids = ids });
            return rows.Select(row => ((IDictionary<string, object>)row).ToDictionary(kvp => kvp.Key, kvp => kvp.Value)).ToList();
        }

        /// <summary>
        /// دالة مساعدة لتحويل الصف (Row) إلى مستند VectorDocument.
        /// </summary>
        private VectorDocument TransformRowToDocument(Dictionary<string, object> row, string tableName)
        {
            string rowAsJson = JsonSerializer.Serialize(row, _jsonOptions);
            string rowId = row.Values.FirstOrDefault()?.ToString() ?? Guid.NewGuid().ToString();

            var metadata = new Dictionary<string, string>
            {
                { VectorDbMeta.SourceType, VectorDbMeta.SourceTypeData },
                { VectorDbMeta.TableName, tableName },
                { VectorDbMeta.RowId, rowId },
                { VectorDbMeta.TenantId, VectorDbMeta.TenantAll }
            };

            if (row.TryGetValue(DbColumns.PageID_Upper, out var pageIdValue) || row.TryGetValue(DbColumns.PageId_Lower, out pageIdValue))
            {
                if (pageIdValue != null) metadata[VectorDbMeta.PageId] = pageIdValue.ToString();
            }
            if (row.TryGetValue(DbColumns.AttributeId, out var attrIdValue) && attrIdValue != null)
            {
                metadata[VectorDbMeta.AttributeId] = attrIdValue.ToString();
            }
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
                DocumentId = $"Data_{tableName}_{rowId}",
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