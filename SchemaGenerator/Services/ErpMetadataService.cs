using Dapper;
using Microsoft.Data.SqlClient;
using SchemaGenerator.Interfaces;
using SchemaGenerator.Models;
using SchemaGenerator.Models.Erp;

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
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ChangeDatabaseAsync(dbName); // (هام: التأكد من أننا في الداتابيز الصحيحة)

            // --- هذا هو الـ Query الذكي الذي يجمع كل "هيكل" الصفحة ---
            // (بناءً على الـ JSON الذي أرسلته)
            const string query = @"
                SELECT 
                    p.PageID, 
                    p.PageDisplay, 
                    m.MEnName AS ModuleName, 
                    mm.MEnName AS MenuMasterName,

                    pa.AttributeId,
                    pa.ListName AS MasterListName, 
                    pa.TableName AS MasterViewName, 
                    pa.TailCondition AS MasterTailCondition,
                    pa.Primary AS MasterPrimaryKey,

                    pd.DetailId, 
                    pd.ListName AS DetailListName, 
                    pd.TableName AS DetailViewName, 
                    pd.TailCondition AS DetailTailCondition,
                    pd.ForeignKey AS DetailForeignKey
                FROM 
                    pages p
                LEFT JOIN 
                    MenuMaster mm ON p.menuID = mm.MID
                LEFT JOIN 
                    Modul m ON mm.ModulID = m.MID
                LEFT JOIN 
                    PageAttribute pa ON p.PageID = pa.PageId
                LEFT JOIN 
                    PageDetail pd ON pa.AttributeId = pd.PageAttributeId
                WHERE 
                    p.PageID IN @TargetPageIDs;
            ";

            var results = await connection.QueryAsync<PageStructureDto>(query, new { TargetPageIDs = targetPageIds });

            // تجميع النتائج: نحول الـ DTOs (النتائج المسطحة) إلى كائنات (ErpPageContext) هرمية
            var groupedByPage = results.GroupBy(r => r.PageID);

            foreach (var pageGroup in groupedByPage)
            {
                var first = pageGroup.First();
                if (string.IsNullOrEmpty(first.MasterViewName)) continue; // (نتجاهل الصفحات التي ليس لها View)

                var context = new ErpPageContext
                {
                    PageId = first.PageID,
                    PageName = first.PageDisplay,
                    ModuleName = first.ModuleName,
                    MenuMasterName = first.MenuMasterName,

                    // (هام) جلب البيانات من PageAttribute (مثل TailCondition)
                    ViewName = first.MasterViewName,
                    ListName = first.MasterListName,
                    PrimaryKey = first.MasterPrimaryKey,
                    TailCondition = first.MasterTailCondition
                };

                // إضافة الـ Details (من PageDetail)
                foreach (var detailRow in pageGroup.Where(d => d.DetailId.HasValue).GroupBy(d => d.DetailId))
                {
                    var detailFirst = detailRow.First();
                    context.DetailContexts.Add(new ErpDetailContext
                    {
                        ViewName = detailFirst.DetailViewName,
                        ListName = detailFirst.DetailListName,
                        ForeignKeyToMaster = detailFirst.DetailForeignKey,
                        TailCondition = detailFirst.DetailTailCondition
                    });
                }

                // (مهم) نضيف الـ View الرئيسي للقاموس
                if (!contextMap.ContainsKey(context.ViewName))
                {
                    contextMap[context.ViewName] = context;
                }
            }
            return contextMap;
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
* Key: "ContractViewWithPaid"
* Value (ErpPageContext object): {
* PageId: 244,
* PageName: "صفحة المبيعات",
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