using SchemaGenerator.Models;

namespace SchemaGenerator.Interfaces
{
    /// <summary>
    /// الواجهة الخاصة بـ "الذاكرة" (State) لخط الأنابيب.
    /// </summary>
    public interface IStateService
    {
        // --- Generic Methods ---
        Task<T?> LoadStateAsync<T>(string key) where T : class;
        Task SaveStateAsync<T>(string key, T state) where T : class;

        // --- Pipeline-Specific Methods ---
        Task<PipelineState> LoadStateAsync(string stateFilePath);
        Task SaveStateAsync(string stateFilePath, PipelineState currentState);
        Task<string> GetLastLsnForTable(string stateFilePath, string tableName);
        Task SetLastLsnForTable(string stateFilePath, string tableName, string lsn);

        // --- [FIXED] Updated method signatures ---
        /// <summary>
        /// (مُعدل) دالة مساعدة لجلب آخر تاريخ تعديل مسجل لكائن سكيما معين.
        /// </summary>
        Task<DateTime?> GetLastDateForSchemaAsync(string stateFilePath, string objectName);

        /// <summary>
        /// (مُعدل) دالة مساعدة لتحديث أو إضافة تاريخ التعديل لكائن سكيما معين.
        /// </summary>
        Task SetLastDateForSchemaAsync(string stateFilePath, string objectName, DateTime lastModified);
    }
}