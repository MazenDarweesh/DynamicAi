using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using SchemaGenerator.Models.Erp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SchemaGenerator.Services
{
    /// <summary>
    /// التنفيذ الفعلي لـ IErpMetadataService.
    /// يستخدم Dapper لتنفيذ Query معقد يجمع بيانات 5 جداول (Pages, PageAttribute, PageDetail, Modul, MenuMaster)
    /// بناءً على الـ PageIDs المستهدفة، ويحولها إلى "خريطة سياق" منظمة.
    /// </summary>
    public class ErpMetadataService : IErpMetadataService
    {
        private readonly ILogger<ErpMetadataService> _logger;

        public ErpMetadataService(ILogger<ErpMetadataService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// يبني "خريطة سياق" (Context Map) من قائمة PageIDs المستهدفة.
        /// </summary>
        public async Task<Dictionary<string, ErpPageContext>> BuildContextMapAsync(
            string connectionString,
            string dbName,
            List<int> targetPageIds)
        {
            var contextMap = new Dictionary<string, ErpPageContext>();
            if (targetPageIds == null || !targetPageIds.Any())
                return contextMap;

            _logger.LogInformation("Building context map from database...");
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ChangeDatabaseAsync(dbName);

            const string query = @"
                WITH PageContext AS (
                    SELECT 
                        p.PageID, 
                        p.PageDisplay, 
                        m.MEnName AS ModuleName, 
                        mm.MEnName AS MenuMasterName,
                        pa.AttributeId,
                        pa.ListName AS MasterListName, 
                        pa.TableName AS MasterViewName, 
                        pa.TailCondition AS MasterTailCondition,
                        pa.[Primary] AS MasterPrimaryKey,
                        pd.DetailId, 
                        pd.ListName AS DetailListName, 
                        pd.TableName AS DetailViewName, 
                        pd.TailCondition AS DetailTailCondition,
                        pd.ForeignKey AS DetailForeignKey
                    FROM 
                        pages p
                    LEFT JOIN 
                        MenuMaster mm ON p.MasterID = mm.MID
                    LEFT JOIN 
                        Modul m ON p.ModulID = m.MID
                    LEFT JOIN 
                        PageAttribute pa ON p.PageID = pa.PageId
                    LEFT JOIN 
                        PageDetail pd ON pa.AttributeId = pd.PageAttributeId
                    WHERE 
                        p.PageID IN @TargetPageIDs
                )
                SELECT 
                    pc.*,
                    ls.ColumnName,
                    ls.arColumnLabel,
                    ls.enColumnLabel,
                    ls.ListName AS ContextListName
                FROM 
                    PageContext pc
                LEFT JOIN 
                    ListSetups ls ON ls.ListName = pc.MasterListName OR ls.ListName = pc.DetailListName
                WHERE
                    pc.MasterViewName IS NOT NULL;
            ";

            var results = await connection.QueryAsync<PageStructureDto>(query, new { TargetPageIDs = targetPageIds });
            _logger.LogDebug("DB query returned {RowCount} flat rows", results.Count());

            var groupedByPage = results.GroupBy(r => r.PageID);

            foreach (var pageGroup in groupedByPage)
            {
                var first = pageGroup.First();
                var context = new ErpPageContext
                {
                    PageId = first.PageID,
                    PageName = first.PageDisplay,
                    ModuleName = first.ModuleName,
                    MenuMasterName = first.MenuMasterName,
                    ViewName = first.MasterViewName,
                    ListName = first.MasterListName,
                    PrimaryKey = first.MasterPrimaryKey,
                    TailCondition = first.MasterTailCondition
                };

                _logger.LogInformation("Processing Page Context for PageID: {PageId} ('{PageName}')",
                    context.PageId, context.PageName);
                _logger.LogDebug("  Master View: '{ViewName}' (List: '{ListName}')",
                    context.ViewName, context.ListName);

                // تجميع بيانات القاموس للـ Master View
                var masterColumns = pageGroup.Where(r => r.ContextListName == context.ListName && !string.IsNullOrEmpty(r.ColumnName)).ToList();
                foreach (var row in masterColumns.DistinctBy(r => r.ColumnName))
                {
                    if (!context.ColumnContext.ContainsKey(row.ColumnName))
                    {
                        context.ColumnContext[row.ColumnName] = new ErpColumnContext
                        {
                            ArLabel = row.arColumnLabel,
                            EnLabel = row.enColumnLabel
                        };
                    }
                }
                _logger.LogDebug("  Found {ColumnCount} columns for Master View", context.ColumnContext.Count);

                // تجميع الـ Details وقواميسها
                var detailGroups = pageGroup
                    .Where(d => d.DetailId.HasValue)
                    .GroupBy(d => d.DetailId.Value);

                foreach (var detailGroup in detailGroups)
                {
                    var detailFirst = detailGroup.First();
                    var detailContext = new ErpDetailContext
                    {
                        ViewName = detailFirst.DetailViewName,
                        ListName = detailFirst.DetailListName,
                        ForeignKeyToMaster = detailFirst.DetailForeignKey,
                        TailCondition = detailFirst.DetailTailCondition
                    };

                    _logger.LogDebug("  Processing Detail Context: '{ViewName}' (List: '{ListName}')",
                        detailContext.ViewName, detailContext.ListName);

                    // تجميع بيانات القاموس للـ Detail View
                    var detailColumns = detailGroup.Where(r => r.ContextListName == detailContext.ListName && !string.IsNullOrEmpty(r.ColumnName)).ToList();
                    foreach (var row in detailColumns.DistinctBy(r => r.ColumnName))
                    {
                        if (!detailContext.ColumnContext.ContainsKey(row.ColumnName))
                        {
                            detailContext.ColumnContext[row.ColumnName] = new ErpColumnContext
                            {
                                ArLabel = row.arColumnLabel,
                                EnLabel = row.enColumnLabel
                            };
                        }
                    }
                    _logger.LogDebug("    Found {ColumnCount} columns for Detail View", detailContext.ColumnContext.Count);

                    if (!context.DetailContexts.Any(dc => dc.ViewName == detailContext.ViewName))
                    {
                        context.DetailContexts.Add(detailContext);
                    }
                }

                if (!contextMap.ContainsKey(context.PageName))
                {
                    contextMap[context.PageName] = context;
                }
            }

            _logger.LogInformation("Context Map built successfully. Found {PageCount} unique page contexts", contextMap.Count);
            return contextMap;
        }

        /// <summary>
        /// يجلب IDs الصفحات المفعّل لها AISetup = 1 من جدول Pages.
        /// </summary>
        public async Task<List<int>> GetAIEnabledPageIdsAsync(string connectionString, string dbName)
        {
            _logger.LogInformation("Discovering AI-enabled pages from database...");

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ChangeDatabaseAsync(dbName);

            const string query = "SELECT PageID FROM Pages WHERE AISetup = 1";
            var pageIds = await connection.QueryAsync<int>(query);
            var result = pageIds.ToList();

            _logger.LogInformation("Found {PageCount} AI-enabled pages: [{PageIds}]",
                result.Count, string.Join(", ", result));

            return result;
        }
    }
}
/*
* ==========================================================================================
* SERVICE SUMMARY (With Examples)
* ==========================================================================================
* Input (Example):  dbName = "QualityControl"
* targetPageIds = [244, 7912, 330, 332, 333, 336]
*
* Output (Example): Task<Dictionary<string, ErpPageContext>>.
* Example Entry:
* Key: "Contracts"
* Value (ErpPageContext object): {
* PageId: 244,
* PageName: "Contracts",
* ViewName: "ContractViewWithPaid",
* ListName: "SalestView",
* TailCondition: "(IsResale=0...)",
* DetailContexts: [ { ViewName: "salessystemcontract", ListName: "SalesDetailView" } ]
* }
*
* Called By:        SchemaOrchestrator.cs
*
* Benefit:          This is the "ERP Context Explorer". It implements our "Page-Driven"
* strategy by querying the 6 core metadata tables (Pages, PageAttribute, etc.).
* It builds the essential "Context Map" that tells the Orchestrator
* *which* Views and *which* ListNames to track.
*/