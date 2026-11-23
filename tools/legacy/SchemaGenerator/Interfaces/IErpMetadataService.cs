using SchemaGenerator.Models.Erp;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SchemaGenerator.Interfaces
{
    /// <summary>
    /// مسؤول عن استكشاف "خريطة" الـ ERP.
    /// يقوم بالاستعلام عن جداول (Pages, PageAttribute, PageDetail, Modul, MenuMaster)
    /// لبناء "سياق" (Context) كامل حول الصفحات المستهدفة.
    /// </summary>
    public interface IErpMetadataService
    {
        /// <summary>
        /// يبني "خريطة سياق" (Context Map) من قائمة PageIDs المستهدفة.
        /// </summary>
        /// <param name="connectionString">Connection string للداتابيز</param>
        /// <param name="dbName">اسم الداتابيز (QualityControl)</param>
        /// <param name="targetPageIds">قائمة الـ 6 Page IDs التي سنبدأ بها.</param>
        /// <returns>
        /// قاموس (Dictionary) يربط "اسم الـ View" (مثل "ContractViewWithPaid")
        /// بالكائن (ErpPageContext) الذي يحتوي على كل تفاصيله (PageName, ModuleName, TailCondition...).
        /// </returns>
        Task<Dictionary<string, ErpPageContext>> BuildContextMapAsync(
            string connectionString,
            string dbName,
            List<int> targetPageIds);

        /// <summary>
        /// يجلب IDs الصفحات المفعّل لها AISetup = 1 من جدول Pages.
        /// </summary>
        /// <param name="connectionString">Connection string للداتابيز</param>
        /// <param name="dbName">اسم الداتابيز (QualityControl)</param>
        /// <returns>قائمة بـ PageIDs المفعّل لها الذكاء الاصطناعي.</returns>
        Task<List<int>> GetAIEnabledPageIdsAsync(string connectionString, string dbName);
    }
}