using SchemaGenerator.Models;

namespace SchemaGenerator.Interfaces
{
    /// <summary>
    /// واجهة (Interface) لـ "استراتيجية" تتبع التغييرات.
    /// (هذا يسمح لنا بتبديلها بـ HashingStrategy إذا فشل CDC، كما اقترح النقد #4)
    /// </summary>
    public interface IChangeTrackingStrategy
    {
        /// <summary>
        /// يجلب التغييرات (INSERT, UPDATE, DELETE) لجدول معين منذ آخر نقطة توقف (LSN).
        /// </summary>
        /// <param name="connectionString">Connection string للداتابيز</param>
        /// <param name="dbName">اسم الداتابيز (QualityControl)</param>
        /// <param name="tableName">اسم الجدول (مثل "ListSetups")</param>
        /// <param name="lastLsnBytes">آخر LSN مسجل (من IStateService)</param>
        /// <returns>CdcChangeResult يحتوي على التغييرات والـ LSN الجديد.</returns>
        Task<CdcChangeResult> GetChangesAsync(
            string connectionString,
            string dbName,
            string tableName,
            byte[] lastLsnBytes
        );
    }
}