
namespace SchemaGenerator.Models
{
    /// <summary>
    /// يمثل "الحالة" (State) الكاملة لخط أنابيب التلقيم (Ingestion Pipeline).
    /// يتم حفظ هذا الكائن كملف process_state.json.
    /// </summary>
    public class PipelineState
    {
        /// <summary>
        /// قاموس (Dictionary) لتتبع الهاش (Hash) الخاص بكل
        /// مستند سكيما (Schema Document) تم رفعه.
        /// Key = DocumentId (e.g., "Table_Contract_Chunk_1")
        /// Value = Hash (e.g., "abc123def...")
        /// </summary>
        public Dictionary<string, string> SchemaHashes { get; set; } = new();

        /// <summary>
        /// (الأهم) قاموس لتتبع آخر "نقطة توقف" (LSN - Log Sequence Number)
        /// لكل جدول بيانات (Data Table) نراقبه باستخدام CDC.
        /// Key = TableName (e.g., "ListSetups")
        /// Value = LSN as string (e.g., "0x000123...ABC")
        /// </summary>
        public Dictionary<string, string> DataLastLsn { get; set; } = new();
    }
}