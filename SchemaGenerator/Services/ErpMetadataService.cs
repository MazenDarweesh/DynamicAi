using Dapper;
using Microsoft.Data.SqlClient;
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

            Console.WriteLine("[MetadataService] Building context map from database...");
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
            Console.WriteLine($"[MetadataService]   - DB query returned {results.Count()} flat rows.");

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

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\n[MetadataService] 🏗️  Processing Page Context for PageID: {context.PageId} ('{context.PageName}')");
                Console.ResetColor();
                Console.WriteLine($"  - Master View: '{context.ViewName}' (List: '{context.ListName}')");

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
                Console.WriteLine($"    - Found {context.ColumnContext.Count} columns for Master View.");

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

                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.WriteLine($"  - Processing Detail Context: '{detailContext.ViewName}' (List: '{detailContext.ListName}')");
                    Console.ResetColor();

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
                    Console.WriteLine($"    - Found {detailContext.ColumnContext.Count} columns for Detail View.");

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

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[MetadataService] ✅ Context Map built successfully. Found {contextMap.Count} unique page contexts.");
            Console.ResetColor();
            return contextMap;
        }

        /// <summary>
        /// يجلب IDs الصفحات المفعّل لها AISetup = 1 من جدول Pages.
        /// </summary>
        public async Task<List<int>> GetAIEnabledPageIdsAsync(string connectionString, string dbName)
        {
            Console.WriteLine("[MetadataService] Discovering AI-enabled pages from database...");

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ChangeDatabaseAsync(dbName);

            const string query = "SELECT PageID FROM Pages WHERE AISetup = 1";
            var pageIds = await connection.QueryAsync<int>(query);
            var result = pageIds.ToList();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[MetadataService] ✅ Found {result.Count} AI-enabled pages: [{string.Join(", ", result)}]");
            Console.ResetColor();

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