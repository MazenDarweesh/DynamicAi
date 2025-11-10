using SchemaGenerator.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SchemaGenerator.Interfaces
{
    /// <summary>
    /// مسؤول عن "تنسيق" عملية استخراج بيانات الـ ERP.
    /// هو "المدير" الذي يستخدم (IChangeTrackingStrategy) لجلب التغييرات
    /// ثم يحولها إلى مستندات (VectorDocuments) جاهزة للرفع.
    /// </summary>
    public interface IErpDataExtractorService
    {
        /// <summary>
        /// يستخرج التغييرات من قائمة جداول البيانات ويحولها إلى VectorDocuments.
        /// </summary>
        /// <param name="connectionString">Connection string</param>
        /// <param name="dbName">اسم الداتابيز (QualityControl)</param>
        /// <param name="tableNames">قائمة الجداول المطلوب تتبعها (e.g., "ListSetups", "Pages"...)</param>
        /// <returns>قائمة المستندات (VectorDocuments) الجاهزة للرفع.</returns>
        Task<List<VectorDocument>> ExtractDataChangesAsync(
            string connectionString,
            string dbName,
            IEnumerable<string> tableNames,
             string stateFilePath);
    }
}