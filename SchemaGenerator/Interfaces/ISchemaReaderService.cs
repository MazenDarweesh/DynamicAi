using SchemaGenerator.Models;


namespace SchemaGenerator.Interfaces
{
    /// <summary>
    /// مسؤول عن قراءة "السكيما" (Schema) من قاعدة البيانات (الجداول والـ Views)
    /// باستخدام SMO (SQL Server Management Objects) وتحويلها إلى
    /// مستندات VectorDocuments جاهزة للرفع.
    /// </summary>
    public interface ISchemaReaderService
    {
        /// <summary>
        /// يستخرج الـ Scripts الخاصة بالجداول والـ Views المستهدفة من قاعدة البيانات.
        /// يقوم بتطبيق "التقسيم الذكي" (Smart Chunking) للجداول الضخمة.
        /// </summary>
        /// <param name="connectionString">Connection string للداتابيز</param>
        /// <param name="dbName">اسم الداتابيز (مطلوب لـ SMO)</param>
        /// <param name="targetViewNames">قائمة الـ Views المستهدفة (من محلل الـ ERP)</param>
        /// <param name="targetTableNames">قائمة الجداول المستهدفة (من محلل الـ ERP)</param>
        /// <returns>قائمة مستندات VectorDocument جاهزة للرفع.</returns>
        Task<List<VectorDocument>> ExtractSchemasAsync(
            string connectionString,
            string dbName,
            List<string> targetViewNames,
            List<string> targetTableNames);
    }
}