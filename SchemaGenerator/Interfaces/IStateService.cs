using SchemaGenerator.Models;

namespace SchemaGenerator.Interfaces
{
    /// <summary>
    /// الواجهة الخاصة بـ "الذاكرة" (State) لخط الأنابيب.
    /// مسؤولة عن قراءة وكتابة آخر حالة (LSN و Hashes) من/إلى ملف JSON
    /// لضمان عدم تكرار العمل (Incremental Updates).
    /// </summary>
    public interface IStateService
    {
        // --- (جديد) Generic Methods for Flexible State Storage ---
        
        /// <summary>
        /// (جديد) يحمّل حالة من نوع محدد باستخدام مفتاح (Key).
        /// </summary>
        /// <typeparam name="T">نوع الحالة (e.g., HashSet<string>, Dictionary<string,int>)</typeparam>
        /// <param name="key">المفتاح الفريد للحالة (e.g., "QdrantUploadedDocumentIds")</param>
        /// <returns>الحالة المحملة، أو null إذا لم توجد</returns>
        Task<T?> LoadStateAsync<T>(string key) where T : class;
        
        /// <summary>
        /// (جديد) يحفظ حالة من نوع محدد باستخدام مفتاح (Key).
        /// </summary>
        /// <typeparam name="T">نوع الحالة</typeparam>
        /// <param name="key">المفتاح الفريد للحالة</param>
        /// <param name="state">الحالة المراد حفظها</param>
        Task SaveStateAsync<T>(string key, T state) where T : class;

        // --- Existing Pipeline-Specific Methods ---
        
        /// <summary>
        /// يقرأ ملف الحالة (JSON) من المسار المحدد ويحوله إلى كائن PipelineState.
        /// </summary>
        /// <param name="stateFilePath">المسار الكامل لملف process_state.json</param>
        /// <returns>كائن الحالة (PipelineState). سيرجع كائن جديد فارغ إذا لم يكن الملف موجوداً.</returns>
        Task<PipelineState> LoadStateAsync(string stateFilePath);

        /// <summary>
        /// يحفظ كائن الحالة (PipelineState) بالكامل في ملف الـ JSON.
        /// </summary>
        /// <param name="stateFilePath">المسار الكامل للملف</param>
        /// <param name="currentState">كائن الحالة المراد حفظه</param>
        Task SaveStateAsync(string stateFilePath, PipelineState currentState);

        /// <summary>
        /// دالة مساعدة (Helper) لجلب آخر LSN مسجل لجدول معين.
        /// </summary>
        /// <param name="stateFilePath">المسار الكامل للملف</param>
        /// <param name="tableName">اسم الجدول (e.g., "ListSetups")</param>
        /// <returns>الـ LSN الأخير كنص، أو null إذا لم يوجد.</returns>
        Task<string> GetLastLsnForTable(string stateFilePath, string tableName);

        /// <summary>
        /// دالة مساعدة (Helper) لتحديث أو إضافة الـ LSN لجدول معين.
        /// (هذه الدالة آمنة للاستخدام المتزامن - Thread-Safe).
        /// </summary>
        Task SetLastLsnForTable(string stateFilePath, string tableName, string lsn);

        /// <summary>
        /// دالة مساعدة (Helper) لجلب آخر Hash مسجل لمستند سكيما معين.
        /// </summary>
        /// <param name="stateFilePath">المسار الكامل للملف</param>
        /// <param name="schemaId">المعرف الفريد للمستند (e.g., "Table_Contract_Chunk_1")</param>
        /// <returns>الـ Hash الأخير، أو null إذا لم يوجد.</returns>
        Task<string> GetLastHashForSchema(string stateFilePath, string schemaId);

        /// <summary>
        /// دالة مساعدة (Helper) لتحديث أو إضافة الـ Hash لمستند سكيما معين.
        /// (هذه الدالة آمنة للاستخدام المتزامن - Thread-Safe).
        /// </summary>
        Task SetLastHashForSchema(string stateFilePath, string schemaId, string hash);
    }
}