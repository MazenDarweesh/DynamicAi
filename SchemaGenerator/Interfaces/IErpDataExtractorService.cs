using SchemaGenerator.Models;
using SchemaGenerator.Models.Erp;

namespace SchemaGenerator.Interfaces
{
    /// <summary>
    /// مسؤول عن استخراج "تغييرات البيانات" (Data Changes) من جداول
    /// الـ Metadata الأساسية في الـ ERP (مثل ListSetups, Pages, إلخ).
    /// </summary>
    public interface IErpDataExtractorService
    {
        /// <summary>
        /// (مُعدل) يستخرج البيانات الوصفية المرتبطة مباشرة بالسياق المحدد.
        /// </summary>
        Task<List<VectorDocument>> ExtractDataChangesAsync(
            string connectionString,
            string dbName,
            Dictionary<string, ErpPageContext> contextMap,
            string stateFilePath,
            string tenantId = "ALL");
    }
}